# Soumen

FF14 Dalamud 实用工具。

工具面板包含移动、战斗、钓鱼动画和战场透视功能。开关及参数会保存并在下次启动时恢复。部分原生功能仅在采集到的游戏客户端版本上运行；打开“关于 → 开发者模式 → 诊断模式”可查看 Hook 安装情况。功能清单和接入方式见 [工具说明](docs/Tools.md)。

后续对话接续项目时，可先阅读 [开发交接说明](docs/CONVERSATION_HANDOFF.md)。

## 安装

在 Dalamud 自定义插件仓库中添加：

```text
https://raw.githubusercontent.com/MusicYYin/DalamudPlugins/main/pluginmaster.json
```

保存后搜索 `Soumen`。

## 依赖

- `vnavmesh`：路线导航。
- `Lifestream`：狩猎换线与跨服。
- `AE Assist`：可选，导航中临时关闭自动选目标。
- 寻宝的自动掷点与藏宝图定位可分别配合 `LazyLoot`、`Globetrotter` 等插件。

## 命令

`/soumen` 打开面板。
