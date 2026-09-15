"""BullBull Unpacker —— 核心逻辑层。

本层完全独立于 UI：任何模块都不得 import ui.*。
这样 core 可在无界面环境下直接被 CLI / 测试驱动。
"""

__all__ = ["theme", "probe", "naming"]
