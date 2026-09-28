# Soumen 后续对话接续说明

用户新开对话时可直接把本文件的 GitHub 链接发给助手，并说明最新要改的功能。本文件是项目交接索引；**开始开发前先查看 GitHub `main` 分支上的实际代码、发行版和插件仓库版本**，不要依赖文中的版本号推断当前状态。

## 截至本次提交

- 源码仓库：`MusicYYin/Soumen`；Dalamud 自定义插件仓库：`MusicYYin/DalamudPlugins`，索引文件 `pluginmaster.json`。
- 本次目标版本：**0.5.7.20**。如果本次发布流程已经完成，源仓库 release 包与索引版本均应为 0.5.7.20；若不一致，先检查发布/索引状态。上一已发布版本为 0.5.7.19。
- 本次改动：战斗功能中的“普通技能余量”改名“普通技能增加量”；战场透视移除“显示战意图标”和名字左侧的红点，按 `Battalion` 0/1/2 分别把敌方名字和透视线画为红/黄/蓝；左侧导航背景与主窗口统一。`Battalion` 到具体军团的 **0=黑涡团、1=双蛇党、2=恒辉队** 映射目前根据现有代码及游戏阵营顺序推定，**尚待游戏实测对照**。旧配置里的战意图标字段在加载时清理。
- 已知验证边界：Windows CI 可以验证构建；战场里阵营颜色、透视线、技能距离/突进、跟随等运行结果必须由用户在游戏里验证。测试日志中地图 888、角色自身 `Battalion=1`，附近三组队伍的编号是 0、1、2；这些数据只能证明有三组，不能单独证明军团名称的映射。

## 源码入口

| 部分 | 文件 | 作用 |
| --- | --- | --- |
| 插件初始化与诊断 | `Soumen/Plugin.cs`、`Soumen/Services/DiagnosticLogger.cs` | Dalamud 服务、`/soumen` 命令、服务生命周期与诊断日志 |
| 状态与持久化 | `Soumen/Configuration.cs` | 配置读取、保存、旧配置迁移、工具开关和滑块范围 |
| 窗口 | `Soumen/Windows/MainWindow.cs`、`Soumen/Models/UiTheme.cs` | 左侧寻宝/狩猎/工具/关于、工具分类、主题、开发者模式 |
| PvP 透视 | `Soumen/Services/FrontlineRadarService.cs` | Dalamud UI 绘制回调，从已加载玩家的 `BattleChara.Battalion` 判断敌我，绘制名字/图标/透视线；无原生 Hook |
| 工具 Hook | `Soumen/Services/ToolCombatService.cs`、`ToolMovementService.cs`、`ToolCastRecastService.cs` 等 | 距离、目标半径、移动、复唱等；固定地址按客户端模块大小/原始字节校验 |
| 兼容性 | `Soumen/Services/ToolAvailabilityService.cs`、`ToolHookAddresses.cs`、`ExternalHookGuard.cs` | 功能状态检查不等于“已开启”，验证原生入口与占用情况 |
| 寻宝与外部插件 | `Soumen/Services/MapFlagAutomation.cs`、`LeaderTreasureAutomation.cs`、`TreasureDungeonAutomation.cs`、`ExternalPluginCoordinator.cs` | 导航、车头流程、宝物库、BMR 跟随等 |

详细的工具清单和实现约束见 [Tools.md](Tools.md)，寻宝状态机和依赖见 [ARCHITECTURE.md](ARCHITECTURE.md)。仓库根目录 [README.md](../README.md) 记载安装地址。

## 已确定的用户偏好与实现约束

- 插件名称、日志和代码保持 **Soumen** 命名；标题说明是“实用工具”。命令暂时只用 `/soumen` 打开面板。
- 工具页有已启用功能的集中关闭区、收藏、移动/战斗/其他/战场/传送/功能状态检查；主题默认彩虹，左上角标题动态彩虹。界面文案简洁，不堆叠解释。
- 工具开关、参数和收藏保存并在下次登录恢复。技能无视距离同时覆盖普通技能增加量（0～3y）与突进的距离判定；目标圈半径增加量上限 3y。原生 Hook 在版本不匹配时不应强行安装；功能状态检查需真实反映所有相关入口。
- 战场透视仅显示已由客户端加载且在探测范围内的敌方角色；不能把它描述为读取了全图所有玩家。保留“显示透视线”“显示职业图标”，删除无效的“显示战意图标”。颜色映射若用户反馈不符，应以其战场截图和诊断日志校正 `Battalion` 映射。
- 宝物库脱战跟随由 BossMod Reborn 完成，战斗时取消脱战跟随；没有 BMR 就不启动这段跟随。请先读 `ExternalPluginCoordinator.cs` 的现有状态恢复逻辑再改指令。
- 潜水传送指**在陆地利用潜水状态远距离传送**，另有 Flag 传送；不包含自动任务点传送。“取消浮起动画”因无效已删除，不要重新加入。
- “关于 → 开发者模式”有诊断模式、解读地图测试、打开日志文件夹、通用插件 Hook 快照采集；采集器面向不同插件，不应固定指向单一插件。

## 修改、验证与发布

1. 检查源仓库 `main`、最新 release 与 `DalamudPlugins/pluginmaster.json`；在新分支或独立工作树编辑，必要时查看用户提供的**最新**诊断日志和截图，旧日志可能来自不同版本。
2. 修改后做 `git diff --check`。当前执行环境可能没有 `dotnet`；可通过源仓库 PR 的 **Windows Build CI** 构建验证。原生 Hook 和游戏 UI 的行为仍以用户实测为准。
3. 在源仓库合并通过构建的 PR，发布对应版本并确认 `Soumen.zip` 存在，然后更新 `DalamudPlugins/pluginmaster.json` 的 `AssemblyVersion`、三个 `DownloadLink*` 和 `LastUpdated`，合并索引 PR，最后核对版本一致。
4. 回复用户时说明已实际完成与仅待实测的部分，附发布地址和本交接文件地址。用户通常持续测多个功能并汇总反馈，不需要每做一项就中断让他测试。

交接文件包含入口与项目偏好，不保存游戏登录信息、授权凭据或用户隐私数据。
