using Dalamud.Game.Command;
using System.Diagnostics;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Soumen.Services;
using Soumen.Windows;

namespace Soumen;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/soumen";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("Soumen");
    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private readonly MapFlagAutomation automation;
    private readonly PartyTeleportService partyTeleportService;
    private readonly TreasureDungeonAutomation treasureDungeonAutomation;
    private readonly TreasureSackAutomation treasureSackAutomation;
    private readonly LeaderTreasureAutomation leaderTreasureAutomation;
    private readonly AutoDiscardService autoDiscardService;
    private readonly StatisticsService statisticsService;
    private readonly HuntAutomation huntAutomation;
    private readonly FrontlineRadarService frontlineRadarService;
    private readonly ToolAvailabilityService toolAvailabilityService = new();
    private readonly ToolCombatService toolCombatService;
    private readonly ToolFishingService toolFishingService;
    private readonly ToolMovementService toolMovementService;
    private readonly ToolStatusService toolStatusService;
    private readonly ToolVerticalService toolVerticalService;
    private readonly ToolMovingCastService toolMovingCastService;
    private readonly ToolCastRecastService toolCastRecastService;
    private readonly CoordinateTeleportService coordinateTeleportService;
    private readonly MainWindow mainWindow;
    private DateTime lastToolHealthUtc;

    public Plugin()
    {
        configuration = Configuration.Load(PluginInterface);
        diagnostics = new DiagnosticLogger(configuration);
        diagnostics.Write("运行", "Soumen 已加载。");
        automation = new MapFlagAutomation(configuration, diagnostics);
        partyTeleportService = new PartyTeleportService(configuration, automation.PrepareForPartyTeleport,
            automation.CanAcceptPartyTeleport, diagnostics);
        treasureDungeonAutomation = new TreasureDungeonAutomation(configuration);
        treasureSackAutomation = new TreasureSackAutomation(configuration, automation, diagnostics);
        leaderTreasureAutomation = new LeaderTreasureAutomation(configuration, automation, treasureSackAutomation, diagnostics);
        autoDiscardService = new AutoDiscardService(configuration, automation, diagnostics);
        statisticsService = new StatisticsService(configuration);
        huntAutomation = new HuntAutomation(configuration, automation, diagnostics);
        frontlineRadarService = new FrontlineRadarService(configuration, diagnostics);
        toolCombatService = new ToolCombatService(configuration, diagnostics);
        toolFishingService = new ToolFishingService(configuration, diagnostics);
        toolMovementService = new ToolMovementService(configuration, diagnostics);
        toolStatusService = new ToolStatusService(configuration, diagnostics);
        toolVerticalService = new ToolVerticalService(configuration, diagnostics);
        toolMovingCastService = new ToolMovingCastService(configuration, diagnostics);
        toolCastRecastService = new ToolCastRecastService(configuration, diagnostics);
        coordinateTeleportService = new CoordinateTeleportService(diagnostics);
        mainWindow = new MainWindow(configuration, automation, leaderTreasureAutomation, autoDiscardService,
            statisticsService, diagnostics, huntAutomation, toolAvailabilityService.Check, coordinateTeleportService);
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 Soumen 面板",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
        Framework.Update += LogToolHealth;
    }

    public void Dispose()
    {
        Framework.Update -= LogToolHealth;
        diagnostics.Write("运行", "Soumen 正在卸载。");
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        frontlineRadarService.Dispose();
        toolCombatService.Dispose();
        toolFishingService.Dispose();
        toolMovementService.Dispose();
        toolStatusService.Dispose();
        toolVerticalService.Dispose();
        toolMovingCastService.Dispose();
        toolCastRecastService.Dispose();
        huntAutomation.Dispose();
        statisticsService.Dispose();
        autoDiscardService.Dispose();
        leaderTreasureAutomation.Dispose();
        treasureSackAutomation.Dispose();
        treasureDungeonAutomation.Dispose();
        partyTeleportService.Dispose();
        automation.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        _ = command;
        _ = args;
        mainWindow.IsOpen = true;
    }

    private void OpenMainUi() => mainWindow.IsOpen = true;

    private void LogToolHealth(IFramework framework)
    {
        _ = framework;
        if (!configuration.DiagnosticMode || DateTime.UtcNow - lastToolHealthUtc < TimeSpan.FromSeconds(20)) return;
        lastToolHealthUtc = DateTime.UtcNow;
        var selected = new List<string>();
        if (configuration.ToolSpeedEnabled) selected.Add("移速");
        if (configuration.ToolMaxAcceleration) selected.Add("最大加速度");
        if (configuration.ToolForceMovement) selected.Add("强制移动");
        if (configuration.ToolAntiKnockback) selected.Add("防击退");
        if (configuration.ToolNoFallDamage) selected.Add("掉落无伤");
        if (configuration.ToolVerticalMovement) selected.Add("飞天遁地");
        if (configuration.ToolNoDrop) selected.Add("无掉落");
        if (configuration.ToolIgnoreCharm) selected.Add("无视魅惑恐惧");
        if (configuration.ToolStatusBlock) selected.Add("状态屏蔽");
        if (configuration.ToolMovingCast) selected.Add("移动读条");
        if (configuration.ToolActionRangeEnabled) selected.Add("技能无视距离");
        if (configuration.ToolTargetRadiusEnabled) selected.Add("目标圈大小");
        if (configuration.NoBackswingMovement) selected.Add("后摇可移动");
        if (configuration.ToolNoActionMove) selected.Add("突进无位移");
        if (configuration.ToolRecastReduction) selected.Add("复唱缩减");
        if (configuration.ToolRecastReduction && configuration.ToolRapidMudra) selected.Add("快速结印");
        if (configuration.ToolCastReduction) selected.Add("咏唱缩减");
        if (configuration.CancelFishingAnimation) selected.Add("取消钓鱼后摇");
        if (configuration.CancelGatheringAnimation) selected.Add("取消采集后摇");
        if (configuration.FrontlineRadarEnabled) selected.Add("战场透视");
        if (selected.Count == 0) return;
        using var process = Process.GetCurrentProcess();
        diagnostics.Write("工具状态", $"已开启：{string.Join("、", selected)}；客户端模块=0x{process.MainModule?.ModuleMemorySize ?? 0:X}；地图={ClientState.TerritoryType}；PvP={ClientState.IsPvP}。");
    }

}
