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
DEFAULT_EXCLUDE_EXTS = (
    "apk", "xapk", "apks", "iso", "img", "dmg", "msi", "deb", "rpm", "jar",
)


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
    min_free_gb: float = 5.0
    flatten: bool = True
    remove_source: bool = False
    remove_intermediate: bool = True   # 解压后删掉嵌套在里面的压缩包（省磁盘）
    clean_delete: bool = True
    # 认不认「前面垫了真视频、后面接压缩包」的伪装（1067.mp4 那种）。
    # 开着要多读一遍非压缩包的文件，但这是这类伪装的唯一识别途径。
    scan_appended: bool = True

    # 额外产物
    # 注：make_html 还没实现（界面上的选项已经撤掉了），字段留着只为读旧配置不报错
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
    # 顺序固定为「文件名 → 记住的密码 → 我添加的密码 → 空密码」，
    # 不需要用户理解，也不需要调。老配置里残留这个字段会被安全忽略。

    # 运行期不持久化的字段放这里（用不到就忽略）
    _path: str = ""

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
            if key not in known:
                continue          # 忽略未知字段：老配置不炸，新字段用默认
            current = getattr(cfg, key)
            try:
                if isinstance(current, bool):
                    setattr(cfg, key, bool(value))
                elif isinstance(current, int):
                    setattr(cfg, key, int(value))
                elif isinstance(current, float):
                    setattr(cfg, key, float(value))
                elif isinstance(current, list):
                    if isinstance(value, list):
                        setattr(cfg, key, list(value))
                elif isinstance(current, str):
                    setattr(cfg, key, str(value))
            except (TypeError, ValueError):
                continue          # 类型不对就用默认值，别把配置读崩
        cfg._path = path
        return cfg

    def save(self, path: str | None = None) -> bool:
        target = path or self._path
        if not target:
            return False
        data: dict[str, Any] = {
            f.name: getattr(self, f.name)
            for f in fields(self)
            if not f.name.startswith("_")
        }
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
        return {
            f.name: getattr(self, f.name)
            for f in fields(self)
            if not f.name.startswith("_")
        }
