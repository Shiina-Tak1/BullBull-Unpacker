"""Qt 工作线程：把 `core.pipeline.Runner` 搬到后台，用信号跟界面通信。

要点：

  * **绝不在界面线程里跑解压**——`subprocess` 一调用就是几百毫秒到几十分钟，
    放主线程整个窗口会假死。所有 core 调用都在 `run()` 里。
  * **回调一律走信号**：跨线程 emit 时 Qt 自动用队列连接，槽函数在界面线程执行，
    所以界面代码不需要加锁。
  * **「问用户要密码」是一个跨线程握手**：工作线程发信号后阻塞在一个
    `threading.Event` 上，界面弹窗拿到结果再回填。这样 core 完全不用知道 Qt 的存在。
"""

from __future__ import annotations

import threading
from dataclasses import dataclass

from PySide6.QtCore import QThread, Signal

from core.config import Config
from core.engine import Extractor
from core.pipeline import Runner, RunnerHooks, ScanItem
from core.vault import PasswordVault, UnlockResult


@dataclass
class AskRequest:
    """「需要密码」弹窗需要的信息：哪个包、试过哪些（只在日志里报个大概）。

    注意这里**别再往回加"把试过的密码逐条列给用户看"**：以前弹窗里要渲染这份清单，
    用的是 `cand.masked`，而 masked 在"取消打码"那一轮就被删了 —— 于是弹窗
    **构造时**抛 AttributeError，pythonw 下没有控制台、异常静默，
    用户看到的现象是「该弹窗的时候没弹」。弹窗越简单，这条路越不容易再断。
    """

    archive: str
    unlock: UnlockResult


class JobWorker(QThread):
    """跑一批任务。界面只需要接信号。"""

    sig_log = Signal(str)
    sig_debug = Signal(str)            # 引擎细节（命令行/原始输出/心跳）：只进文件
    sig_item = Signal(object)          # ScanItem
    sig_ask = Signal(object)           # AskRequest
    sig_done = Signal(object)          # list[ScanItem]

    def __init__(
        self,
        items: list[ScanItem],
        vault: PasswordVault,
        extractor: Extractor,
        config: Config,
        parent=None,
    ) -> None:
        super().__init__(parent)
        self.items = items
        self.vault = vault
        self.extractor = extractor
        self.config = config
        self._stop = False
        self._kill = False
        self._paused = False
        self._answer: str | None = None
        self._answer_ready = threading.Event()

    # -- 界面线程调用 --------------------------------------------------

    def request_pause(self) -> None:
        """暂停：把正在跑的引擎进程**挂起**，并且不再开始下一个任务。"""
        self._paused = True

    def request_resume(self) -> None:
        """继续：恢复被挂起的引擎进程，接着往后跑。"""
        self._paused = False

    def is_paused(self) -> bool:
        return self._paused

    def request_stop(self) -> None:
        """停止：立刻掐断正在跑的引擎子进程，并结束整批。"""
        self._stop = True
        self._kill = True
        self._paused = False           # 别让挂起状态挡住"杀掉"
        self._answer_ready.set()      # 万一正卡在问密码上，也放它走

    def answer_password(self, password: str | None) -> None:
        """界面拿到用户输入后回填（None = 用户放弃这个任务）。"""
        self._answer = password
        self._answer_ready.set()

    def should_stop(self) -> bool:
        """任务之间检查：还要不要继续下一个。"""
        return self._stop

    def should_pause(self) -> bool:
        """正在跑的引擎会轮询它：True 就先挂起。"""
        return self._paused and not self._stop

    def should_kill(self) -> bool:
        """给引擎用：要不要把正在跑的子进程杀掉。"""
        return self._kill

    # -- 工作线程 ------------------------------------------------------

    def _ask(self, archive: str, un: UnlockResult) -> str | None:
        """被 core 调用；转成信号发给界面，然后等界面回话。"""
        if self._stop:
            return None
        # 暂停中不该弹窗要密码——先把挂起状态让出去，等恢复再说
        while self.should_pause():
            threading.Event().wait(0.1)
        self._answer = None
        self._answer_ready.clear()
        self.sig_ask.emit(AskRequest(archive=archive, unlock=un))
        # 等用户回答；超时就当放弃，避免线程永远挂着
        self._answer_ready.wait(timeout=900)
        return self._answer

    def run(self) -> None:  # noqa: D102 - QThread 入口
        hooks = RunnerHooks(
            on_log=self.sig_log.emit,
            on_item=self.sig_item.emit,
            ask_password=self._ask,
        )
        # 引擎的"掐断"和"挂起"回调单独给：
        #   cancel 要的是 should_kill（立刻杀进程），Runner 的 should_stop 只在任务之间检查
        #   pause 要的是 should_pause（真挂起子进程）
        self.extractor.cancel = self.should_kill
        self.extractor.pause = self.should_pause
        # ★ 引擎自己的话也分两条通道：
        #   logger       —— 挂起/恢复这种用户该看见的（进界面）
        #   debug_logger —— 命令行、原始输出、心跳（**不进界面**，只进 run.log；
        #                   界面里要看就打开「详细」）。命令行里带 `-p<密码>`，
        #                   以前一股脑写进界面，等于把密码本摊在屏幕上。
        self.extractor.logger = self.sig_log.emit
        self.extractor.debug_logger = self.sig_debug.emit
        runner = Runner(
            self.items,
            vault=self.vault,
            extractor=self.extractor,
            config=self.config,
            hooks=hooks,
            should_stop=self.should_stop,
            should_pause=self.should_pause,
        )
        try:
            result = runner.run()
        except Exception as exc:                      # 兜底：别让线程带着异常静默死掉
            self.sig_log.emit(f"✘ 内部错误：{exc!r}")
            result = self.items
        finally:
            self.extractor.cancel = None
            self.extractor.pause = None
            self.extractor.logger = None
            self.extractor.debug_logger = None
        self.sig_done.emit(result)
