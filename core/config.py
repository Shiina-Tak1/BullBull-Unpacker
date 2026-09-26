"""配置持久化：config.json。

放在工程根目录，和「密码本.txt」同级。缺字段一律回落到默认值，
所以旧版本的 config.json 在新版本里不会炸——自用工具最常见的坑就是
加了个字段之后老配置读不出来。
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field, fields
from typing import Any

DEFAULT_FILENAME = "config.json"

# 默认不处理的扩展名。
#
# 为什么需要这个：`.apk` / `.iso` / `.jar` 这些"看着像压缩包"的东西本质就是
# zip（apk=zip、jar=zip），引擎会老老实实把它们解开——可用户要的是"解压我下载的
# 压缩包"，不是"把我的安装包拆了"（实测就发生过：apk 被解开成一堆资源文件）。
# 这些仍然**列在清单里**并标明"已排除"，免得用户以为工具没看见他的文件。
#
# ★ Office 文档是同一类（2026-09-26 补）：`docx` / `xlsx` / `pptx` **本身就是 zip**，
#   引擎照拆不误 —— 实测一个素材包解到第 3 层时，里面的 docx 被拆成
#   `报告/word/document.xml`，而「删除解出来的嵌套包」又把源 docx 一并删掉（用户丢的是
#   自己的文档，不是他下载的压缩包）。所以它们进默认名单，语义与上面那批完全一致：
#   **仍然列在清单里**、标「已排除」，只是不再解压。
#   `doc` / `xls` / `ppt` 是老式 OLE 复合文档、引擎本来也拆不开，把它们一起列进来
#   只是为了「Office 文档一律不处理」这条语义统一（不是因为它们会被拆）。
DEFAULT_EXCLUDE_EXTS = (
    "apk", "xapk", "apks", "iso", "img", "dmg", "msi", "deb", "rpm", "jar",
    "doc", "docx", "xls", "xlsx", "ppt", "pptx",
)

# 字段**改名**时的兼容映射：老配置里的旧名 → 现在的新名。
#
# 为什么需要它：`Config.load()` 把不认识的 key 一律当"未知字段"跳过 —— 那是**加字段**的
# 兼容手段（老配置不炸），但"改名"走同一条路就成了**静默丢值**：用户调过的值悄悄回默认，
# 一句提示都没有（可维护性审计报告 R13 点名的场景：把 `workers` 改名，用户实测配的 16
# 会变回 0）。所以**改字段名时必须往这里加一行**，旧名下的值就会被搬到新名下，
# 并在 `Config.migrated` 里留一句人话，界面可以提示一次。
#
# 注意：**删字段不用登记**（那是有意丢弃，见下面 `shell_auto` / `library_order` 两段注释）。
RENAMED: dict[str, str] = {}

# **只读兼容、不再写回**的字段：这些字段现在没有实现 / 界面上也没有入口，留着只为
# "读老配置不报错"。以前 `save()` 会把它们原样写回去，于是用户手动删掉之后下次保存
# 又冒出来（`make_html` 就是这样被反复写回的）。`save()` / `to_dict()` 按这份名单过滤。
META_FIELDS = frozenset({"make_html"})


@dataclass
class Config:
    # 引擎
    seven_zip: str = ""
    winrar: str = ""
    timeout_min: float = 30.0

    # 不当作压缩包处理的扩展名（小写、不带点）。界面上可改。
    exclude_exts: list[str] = field(default_factory=lambda: list(DEFAULT_EXCLUDE_EXTS))

    # 解压行为
    max_depth: int = 5
    min_free_gb: float = 1.0
    flatten: bool = True
    remove_source: bool = False
    remove_intermediate: bool = True   # 解压后删掉嵌套在里面的压缩包（省磁盘）
    clean_delete: bool = True
    # 试密码时的并行路数：0 = 自动（用满本机逻辑核，也就是天花板）。
    # 只在"便宜路径"（只读文件头/只测一个小条目）上并行，整包回退那种 IO 密集的路子仍是串行。
    workers: int = 0
    # 认不认「前面垫了真视频、后面接压缩包」的伪装（示例.mp4 那种）。
    # 开着要多读一遍非压缩包的文件，但这是这类伪装的唯一识别途径。
    scan_appended: bool = True

    # 额外产物
    # 注：make_html 还没实现（界面上的选项已经撤掉了），字段留着只为读旧配置不报错。
    # 它在 `META_FIELDS` 里 —— **只读不写回**，别再让它回到 config.json 里。
    make_html: bool = True

    # 输出与命名
    output_mode: str = "same"        # same | custom
    output_dir: str = ""
    conflict: str = "rename"         # rename | overwrite | skip

    # 界面
    theme: str = "dark"
    language: str = "zh-CN"
    # 主界面表格的列宽（拖过/双击自适应过就记下来，下次打开还是这个宽度）
    table_cols: list[int] = field(default_factory=list)
    # 注：曾经有个 shell_auto（右键后自动开始解压）字段，已经删掉——
    # 右键菜单现在只有一种模式：把路径加进待处理列表，不自动开始。
    # 老配置里残留这个字段会被安全忽略（load 只认已知字段）。

    # 注：曾经有个 library_order（密码来源优先级）字段，已经删掉——
    # 顺序固定为「文件名 → 密码本 → 空密码」，
    # 不需要用户理解，也不需要调。老配置里残留这个字段会被安全忽略。

    # 运行期不持久化的字段放这里（用不到就忽略）
    _path: str = ""
    # 本次 load 里**因为改名而搬过家**的字段（`["旧名 → 新名", …]`），给界面提示一次用。
    # 也是运行期字段（不落盘）：它是"这一次读配置发生过什么"，不是一个设置项。
    _migrated: list[str] = field(default_factory=list)

    @property
    def migrated(self) -> list[str]:
        """本次读配置时按 `RENAMED` 搬过家的字段（没有就是空表）。"""
        return list(self._migrated)

    # -- 读写 ----------------------------------------------------------

    @classmethod
    def load(cls, path: str) -> Config:
        """读配置。文件不存在/损坏/字段多余，都不会抛异常。"""
        cfg = cls(_path=path)
        if not path or not os.path.isfile(path):
            return cfg
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            return cfg
        if not isinstance(data, dict):
            return cfg

        known = {f.name for f in fields(cls) if not f.name.startswith("_")}
        for key, value in data.items():
            target = key
            if key not in known and key in RENAMED:
                # 改名字段：把旧名下的值搬到新名下，并**记下来**（不许静默丢值）
                target = RENAMED[key]
                cfg._migrated.append(f"{key} → {target}")
            if target not in known:
                continue          # 忽略未知字段：老配置不炸，新字段用默认
            current = getattr(cfg, target)
            try:
                if isinstance(current, bool):
                    setattr(cfg, target, bool(value))
                elif isinstance(current, int):
                    setattr(cfg, target, int(value))
                elif isinstance(current, float):
                    setattr(cfg, target, float(value))
                elif isinstance(current, list):
                    if isinstance(value, list):
                        setattr(cfg, target, list(value))
                elif isinstance(current, str):
                    setattr(cfg, target, str(value))
            except (TypeError, ValueError):
                continue          # 类型不对就用默认值，别把配置读崩
        cfg._path = path
        return cfg

    def save(self, path: str | None = None) -> bool:
        target = path or self._path
        if not target:
            return False
        data = self.to_dict()          # 过滤规则只有一份（见 `META_FIELDS`）
        try:
            os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
            tmp = target + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
            os.replace(tmp, target)   # 原子替换，写一半断电也不会毁掉配置
        except OSError:
            return False
        self._path = target
        return True

    # -- 便捷 ----------------------------------------------------------

    @property
    def timeout_seconds(self) -> float:
        return max(1.0, float(self.timeout_min) * 60.0)

    def to_dict(self) -> dict[str, Any]:
        """要落盘的那份字段表。

        排除两类：`_` 开头的运行期字段（`_path` / `_migrated`），以及 `META_FIELDS`
        里那批"只读兼容、不再写回"的字段（`make_html` —— 以前每次保存都把它写回去，
        用户手动删掉之后又会冒出来）。
        """
        return {
            f.name: getattr(self, f.name)
            for f in fields(self)
            if not f.name.startswith("_") and f.name not in META_FIELDS
        }
