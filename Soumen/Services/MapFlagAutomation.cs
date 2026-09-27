using System.Numerics;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Soumen.Models;

namespace Soumen.Services;

public sealed class MapFlagAutomation : IDisposable
{
    private const float NearbyMapCoordinateTolerance = 0.5f;
    private const float ProgressDistance = 2f;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan NavigationRetryInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ActionRetryInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RoutePlanningTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TeleportPrepareDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan TeleportPrepareTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TeleportStartTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TeleportCancelledTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TeleportConfirmationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PartyTeleportTimeout = TimeSpan.FromSeconds(20);
    private const int MaxTeleportAttempts = 2;

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private readonly VNavmeshIpc vnavmesh;
    private readonly TreasureSpotResolver treasureSpots;
    private readonly ExternalPluginCoordinator externalPlugins;
    private readonly TeleportService teleporter;
    private readonly LifestreamIpc lifestream;
    private readonly Dictionary<string, MapFlagTarget> destinations = [];

    private MapFlagTarget? activeTarget;
    private Vector3? destination;
    private RoutePlan? routePlan;
    private Vector3 progressAnchor;
    private float progressDistance = float.MaxValue;
    private Vector3? teleportArrivalPosition;
    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime lastMountAttemptUtc = DateTime.MinValue;
    private DateTime lastDismountAttemptUtc = DateTime.MinValue;
    private DateTime lastNavigationAttemptUtc = DateTime.MinValue;
    private DateTime lastProgressUtc = DateTime.UtcNow;
    private DateTime teleportPreparedUtc = DateTime.MinValue;
    private DateTime teleportStationarySinceUtc = DateTime.MinValue;
    private DateTime teleportIssuedUtc = DateTime.MinValue;
    private DateTime? partyTeleportAcceptedUtc;
    private uint? teleportAetheryteId;
    private string? teleportAetheryteName;
    private long? teleportedTargetSerial;
    private long? routeComparedTargetSerial;
    private uint teleportOriginTerritoryId;
    private Vector3? teleportOriginPosition;
    private long serial;
    private int recoveryAttempts;
    private int teleportAttemptCount;
    private bool manualSelection;
    private bool paused;
    private bool teleportSawCasting;
    private bool teleportSawLoading;
    private bool partyTeleportSawLoading;
    private bool dungeonFollowByBossMod;
    private int huntTargetInstance;
    private DateTime lastInstanceRequestUtc = DateTime.MinValue;
    private DateTime lastInstanceTeleportUtc = DateTime.MinValue;
    private bool huntCombatNavigation;
    private bool disposed;
    private uint pendingOwnFlagTerritory;
    private uint pendingOwnFlagMap;
    private float pendingOwnFlagMapX;
    private float pendingOwnFlagMapY;
    private DateTime pendingOwnFlagExpiresUtc = DateTime.MinValue;

    public MapFlagAutomation(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        vnavmesh = new VNavmeshIpc(Plugin.PluginInterface, diagnostics);
        treasureSpots = new TreasureSpotResolver(configuration, diagnostics);
        teleporter = new TeleportService(diagnostics);
        lifestream = new LifestreamIpc(diagnostics);
        externalPlugins = new ExternalPluginCoordinator(configuration, diagnostics);

        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.Framework.Update += OnFrameworkUpdate;

        if (configuration.Enabled)
        {
            externalPlugins.StartRuntime();
            SetState(AutomationState.Idle, "等待小队坐标");
        }
        else
        {
            SetState(AutomationState.Disabled, "自动化已关闭");
        }
    }

    public AutomationState State { get; private set; }

    public string StatusText { get; private set; } = "初始化";

    public MapFlagTarget? ActiveTarget => activeTarget;

    public bool IsManualSelection => manualSelection;

    public bool IsPaused => paused;

    public OperatingMode Mode => configuration.OperatingMode;

    public IReadOnlyList<MapFlagTarget> Destinations
        => destinations.Values.OrderByDescending(target => target.ReceivedAtUtc).ToList();

    public bool VnavmeshInstalled => vnavmesh.IsInstalled;

    public bool VnavmeshReady => vnavmesh.IsReady();

    public bool AeAssistInstalled => externalPlugins.AeAssistInstalled;
    public bool LifestreamInstalled => lifestream.IsInstalled;
    public bool IsHuntApproaching => huntCombatNavigation;

    public void SetHuntCombatNavigation(bool moving)
    {
        huntCombatNavigation = moving;
        externalPlugins.SetNavigating(moving || State == AutomationState.Navigating);
    }

    public bool BossModRebornInstalled => externalPlugins.BossModRebornInstalled;

    public bool LazyLootInstalled => externalPlugins.LazyLootInstalled;

    public bool GlobetrotterInstalled => externalPlugins.GlobetrotterInstalled;

    public bool DailyRoutinesInstalled => externalPlugins.DailyRoutinesInstalled;

    public bool MapLocatorInstalled => GlobetrotterInstalled || DailyRoutinesInstalled;

    public event Action<MapFlagTarget>? DestinationReached;
    public event Action<AutomationTask>? TaskActivated;

    public void SetLazyLootRollMode(LazyLootRollMode mode)
    {
        if (configuration.LazyLootRollMode == mode)
        {
            return;
        }

        configuration.LazyLootRollMode = mode;
        configuration.Save();
        if (configuration.Enabled && !configuration.HuntEnabled)
        {
            externalPlugins.ApplyLazyLootRollMode();
        }
    }

    public void SetOperatingMode(OperatingMode mode)
    {
        if (configuration.OperatingMode == mode && !configuration.HuntEnabled)
        {
            return;
        }

        configuration.OperatingMode = mode;
        if (configuration.Enabled)
        {
            ActivateTask(mode == OperatingMode.Leader ? AutomationTask.TreasureLeader : AutomationTask.TreasureFollow);
            return;
        }
        configuration.Save();
    }

    public void ActivateTask(AutomationTask task)
    {
        if (configuration.ActiveTask == task) return;
        ResetNavigation(clearDestinations: true);
        externalPlugins.StopRuntime();
        configuration.ActiveTask = task;
        configuration.Enabled = task != AutomationTask.None;
        configuration.HuntEnabled = task == AutomationTask.HuntTrain;
        if (task == AutomationTask.TreasureLeader) configuration.OperatingMode = OperatingMode.Leader;
        else if (task != AutomationTask.None) configuration.OperatingMode = OperatingMode.Follow;
        if (configuration.Enabled) externalPlugins.StartRuntime();
        configuration.Save();
        SetState(configuration.Enabled ? AutomationState.Idle : AutomationState.Disabled,
            task switch
            {
                AutomationTask.TreasureFollow => "寻宝跟车已开启",
                AutomationTask.TreasureLeader => "寻宝车头已开启",
                AutomationTask.HuntTrain => "狩猎跟车已开启",
                _ => "自动化已关闭",
            });
        diagnostics.Write("任务", $"启用任务：{task}。");
        TaskActivated?.Invoke(task);
    }

    public unsafe MapFlagTarget? RegisterOwnFlagFromGame()
    {
        var agent = AgentMap.Instance();
        if (agent == null || agent->FlagMarkerCount == 0)
        {
            return null;
        }

        var marker = agent->FlagMapMarkers[0];
        if (marker.TerritoryId == 0 || marker.MapId == 0)
        {
            return null;
        }

        var rawX = (int)MathF.Round(marker.XFloat * 1000f, MidpointRounding.AwayFromZero);
        var rawY = (int)MathF.Round(marker.YFloat * 1000f, MidpointRounding.AwayFromZero);
        var mapX = marker.XFloat;
        var mapY = marker.YFloat;
        var placeName = $"Territory {marker.TerritoryId}";
        try
        {
            if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Map>().TryGetRow(marker.MapId, out var map))
            {
                var mapPoint = MapUtil.WorldToMap(new Vector2(marker.XFloat, marker.YFloat), map);
                mapX = mapPoint.X;
                mapY = mapPoint.Y;
            }

            if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().TryGetRow(marker.TerritoryId, out var territory))
            {
                placeName = territory.PlaceName.ValueNullable?.Name.ToString() ?? placeName;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to resolve own treasure flag display coordinates.");
        }

        var target = new MapFlagTarget(
            ++serial,
            "我（藏宝图）",
            Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "我",
            0,
            0,
            marker.TerritoryId,
            marker.MapId,
            rawX,
            rawY,
            mapX,
            mapY,
            placeName,
            DateTime.UtcNow,
            true);

        destinations[target.SenderKey] = target;
        pendingOwnFlagTerritory = target.TerritoryId;
        pendingOwnFlagMap = target.MapId;
        pendingOwnFlagMapX = target.MapX;
        pendingOwnFlagMapY = target.MapY;
        pendingOwnFlagExpiresUtc = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        if (configuration.OperatingMode == OperatingMode.Leader)
        {
            SelectTarget(target, isManual: false);
        }

        diagnostics.Write(
            "车头",
            $"登记自己的藏宝图坐标：territory={target.TerritoryId}，map={target.MapId}，world=({marker.XFloat:F1},{marker.YFloat:F1})。" );
        return target;
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled) paused = false;
        ActivateTask(enabled
            ? configuration.HuntEnabled ? configuration.ActiveTask
                : configuration.OperatingMode == OperatingMode.Leader
                    ? AutomationTask.TreasureLeader : AutomationTask.TreasureFollow
            : AutomationTask.None);
    }

    public void SetPaused(bool value)
    {
        if (!configuration.Enabled || paused == value)
        {
            return;
        }

        paused = value;
        if (paused)
        {
            vnavmesh.Stop();
            externalPlugins.SetNavigating(false);
            routePlan = null;
            SetState(AutomationState.Paused, activeTarget == null ? "已暂停" : "已暂停，当前目的地已保留");
            return;
        }

        if (activeTarget != null)
        {
            destination = null;
            SetState(AutomationState.WaitingForPlayer, "继续当前目的地");
        }
        else
        {
            SetState(AutomationState.Idle, "等待小队坐标");
        }
    }

    public void Stop(string reason = "导航已停止")
    {
        ResetNavigation(clearDestinations: false);
        SetState(configuration.Enabled ? AutomationState.Idle : AutomationState.Disabled,
            configuration.Enabled ? reason : "自动化已关闭");
    }

    public void PrepareForPartyTeleport()
    {
        if (!configuration.Enabled || paused || activeTarget == null)
        {
            return;
        }

        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        destination = null;
        routePlan = null;
        ResetTeleportState();
        partyTeleportAcceptedUtc = DateTime.UtcNow;
        partyTeleportSawLoading = false;
        SetState(AutomationState.WaitingForPlayer, "已接受队友传送，等待传送后重新寻路");
    }

    public bool CanAcceptPartyTeleport()
        => configuration.Enabled
            && !configuration.HuntEnabled
            && !paused
            && configuration.OperatingMode == OperatingMode.Follow
            && configuration.AcceptPartyTeleportRequests
            && (activeTarget == null || (activeTarget is { IsCrossParty: false } target
                && Plugin.ClientState.TerritoryType == target.TerritoryId))
            && !TreasureContext.IsTreasureDungeon();

    public bool NavigateTo(long targetSerial)
    {
        var target = destinations.Values.FirstOrDefault(entry => entry.Serial == targetSerial);
        if (target == null || !configuration.Enabled)
        {
            return false;
        }

        if (configuration.OperatingMode == OperatingMode.Leader && !target.IsOwnTreasure)
        {
            ActivateTask(AutomationTask.TreasureFollow);
            destinations[target.SenderKey] = target;
            diagnostics.Write("模式", "车头模式下手动选择了队友坐标，已切换到跟车模式。");
        }

        SelectTarget(target, isManual: true);
        return true;
    }

    public void NavigateHunt(MapLinkPayload link, string sender, int instance = 0)
    {
        if (!configuration.HuntEnabled || !configuration.Enabled)
        {
            return;
        }

        huntTargetInstance = instance;
        lastInstanceRequestUtc = DateTime.MinValue;
        lastInstanceTeleportUtc = DateTime.MinValue;
        var target = new MapFlagTarget(++serial, sender, sender, 0, 0,
            link.TerritoryType.RowId, link.Map.RowId, link.RawX, link.RawY,
            link.XCoord, link.YCoord, link.PlaceName, DateTime.UtcNow,
            IsHunt: true);
        destinations[target.SenderKey] = target;
        SelectTarget(target, isManual: false);
        diagnostics.Write("狩猎", $"接收车头坐标：{sender}，{target.PlaceName} ({target.MapX:F1}, {target.MapY:F1})。");
    }

    public void RemoveDestination(long targetSerial)
    {
        var entry = destinations.Values.FirstOrDefault(target => target.Serial == targetSerial);
        if (entry == null)
        {
            return;
        }

        destinations.Remove(entry.SenderKey);
        if (activeTarget?.Serial == targetSerial)
        {
            Stop("已删除当前目的地");
        }
    }

    public float? DistanceTo(MapFlagTarget target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || Plugin.ClientState.TerritoryType != target.TerritoryId)
        {
            return null;
        }

        return HorizontalDistance(player.Position, GetResolvedWorldPosition(target, player.Position.Y));
    }

    public Vector3 GetResolvedWorldPosition(MapFlagTarget target, float fallbackHeight)
        => !target.IsHunt && treasureSpots.TryResolve(target, out var position, out _)
            ? position
            : target.ToWorld(fallbackHeight);

    public int NearbyDestinationCount(MapFlagTarget target)
        => destinations.Values.Count(other => IsNearby(target, other));

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        vnavmesh.Stop();
        externalPlugins.StopRuntime();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!configuration.Enabled || configuration.HuntEnabled
            || message.LogKind is not (XivChatType.Party or XivChatType.CrossParty))
        {
            return;
        }

        var mapLink = message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (mapLink == null)
        {
            return;
        }

        var sender = ResolveSender(message.Sender);
        var senderText = string.IsNullOrWhiteSpace(message.Sender.TextValue)
            ? sender.Name
            : message.Sender.TextValue;
        var rawX = mapLink.RawX;
        var rawY = mapLink.RawY;
        var localPlayerName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;
        var isOwnTreasure = DateTime.UtcNow <= pendingOwnFlagExpiresUtc
            && string.Equals(sender.Name, localPlayerName, StringComparison.OrdinalIgnoreCase)
            && mapLink.TerritoryType.RowId == pendingOwnFlagTerritory
            && mapLink.Map.RowId == pendingOwnFlagMap
            && Math.Abs(mapLink.XCoord - pendingOwnFlagMapX) <= NearbyMapCoordinateTolerance
            && Math.Abs(mapLink.YCoord - pendingOwnFlagMapY) <= NearbyMapCoordinateTolerance;
        var target = new MapFlagTarget(
            ++serial,
            isOwnTreasure ? "我（藏宝图）" : senderText,
            sender.Name,
            sender.WorldId,
            sender.ContentId,
            mapLink.TerritoryType.RowId,
            mapLink.Map.RowId,
            mapLink.RawX,
            mapLink.RawY,
            mapLink.XCoord,
            mapLink.YCoord,
            mapLink.PlaceName,
            DateTime.UtcNow,
            isOwnTreasure,
            message.LogKind == XivChatType.CrossParty);

        var existingOwnTreasure = isOwnTreasure
            ? destinations.Values.FirstOrDefault(existing => existing.IsOwnTreasure && IsNearby(existing, target))
            : null;
        if (existingOwnTreasure != null)
        {
            pendingOwnFlagExpiresUtc = DateTime.MinValue;
            diagnostics.Write(
                "坐标",
                $"自己的小队坐标与藏宝图旗标一致，已合并：map=({target.MapX:F1},{target.MapY:F1})。" );
            return;
        }

        var activeSenderUpdated = activeTarget != null && IsSameSender(activeTarget, target);
        foreach (var duplicate in destinations.Values
                     .Where(existing => IsSameSender(existing, target))
                     .Select(existing => existing.SenderKey)
                     .ToList())
        {
            destinations.Remove(duplicate);
        }

        destinations[target.SenderKey] = target;

        if (activeSenderUpdated)
        {
            SelectTarget(target, manualSelection);
        }
        else if (!manualSelection
                 && (configuration.OperatingMode == OperatingMode.Follow || target.IsOwnTreasure))
        {
            SelectTarget(target, isManual: false);
        }

        Plugin.Log.Information(
            "Received party destination from {Sender}: {Place} {X:F1}, {Y:F1}.",
            target.Sender,
            target.PlaceName,
            target.MapX,
            target.MapY);
        diagnostics.Write(
            "坐标",
            $"收到 {message.LogKind} 坐标：sender={target.Sender}，territory={target.TerritoryId}，map={target.MapId}，raw=({target.RawX},{target.RawY})，map=({target.MapX:F1},{target.MapY:F1})，serial={target.Serial}。");
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        _ = framework;
        var now = DateTime.UtcNow;
        if (now < nextUpdateUtc)
        {
            return;
        }

        nextUpdateUtc = now + UpdateInterval;

        if (!configuration.Enabled)
        {
            return;
        }

        externalPlugins.RefreshRuntime();
        externalPlugins.SetNavigating(huntCombatNavigation || dungeonFollowByBossMod || State == AutomationState.Navigating);

        if (!configuration.HuntEnabled && configuration.OperatingMode == OperatingMode.Follow && TreasureContext.IsTreasureDungeon())
        {
            if (activeTarget != null)
            {
                Stop("已进入宝物库，改为跟随队长");
            }

            ProcessDungeonFollow();
            return;
        }

        StopDungeonFollow();

        if (paused)
        {
            if (State != AutomationState.Paused)
            {
                SetState(AutomationState.Paused, "已暂停");
            }

            return;
        }

        if (activeTarget?.IsHunt == true && Plugin.Condition[ConditionFlag.InCombat])
        {
            if (State != AutomationState.WaitingForPlayer)
            {
                vnavmesh.Stop();
                externalPlugins.SetNavigating(false);
                routePlan = null;
                destination = null;
                SetState(AutomationState.WaitingForPlayer, "狩猎战斗中，结束后前往车头坐标");
            }
            return;
        }

        if (partyTeleportAcceptedUtc != null)
        {
            ProcessPartyTeleport(now);
            return;
        }

        if (activeTarget != null
            && State != AutomationState.Teleporting
            && IsLoadingOrOccupied())
        {
            vnavmesh.Stop();
            externalPlugins.SetNavigating(false);
            destination = null;
            routePlan = null;
            SetState(AutomationState.WaitingForPlayer, "传送或读图中，完成后重新寻路");
            return;
        }

        switch (State)
        {
            case AutomationState.Disabled:
                SetState(AutomationState.Idle, "等待小队坐标");
                break;
            case AutomationState.Idle:
            case AutomationState.Error:
                break;
            case AutomationState.Paused:
                SetState(activeTarget == null ? AutomationState.Idle : AutomationState.WaitingForPlayer,
                    activeTarget == null ? "等待小队坐标" : "继续当前目的地");
                break;
            case AutomationState.WaitingForPlayer:
                ProcessPlayerReady();
                break;
            case AutomationState.PlanningRoute:
                ProcessRoutePlanning();
                break;
            case AutomationState.Teleporting:
                ProcessTeleporting(now);
                break;
            case AutomationState.Mounting:
                ProcessMounting(now);
                break;
            case AutomationState.WaitingForVnavmesh:
                ProcessVnavReady();
                break;
            case AutomationState.Navigating:
                ProcessNavigation(now);
                break;
            case AutomationState.Landing:
                ProcessLanding(now);
                break;
            case AutomationState.Dismounting:
                ProcessDismounting(now);
                break;
        }
    }

    private unsafe void ProcessDungeonFollow()
    {
        if (!configuration.DungeonAutoFollow || paused)
        {
            StopDungeonFollow();
            return;
        }
        if (IsLoadingOrOccupied() || Plugin.Condition[ConditionFlag.InCombat])
        {
            SuspendDungeonFollow();
            return;
        }

        var groupManager = GroupManager.Instance();
        var group = groupManager == null ? null : groupManager->GetGroup();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (group == null || player == null || group->PartyLeaderIndex >= group->MemberCount)
        {
            diagnostics.WriteThrottled("bmr-no-party-leader", "BMR跟随",
                "尚未取得小队队长信息。", TimeSpan.FromSeconds(10));
            SuspendDungeonFollow();
            return;
        }

        var leaderId = group->PartyMembers[(int)group->PartyLeaderIndex].EntityId;
        var leader = Plugin.ObjectTable.FirstOrDefault(obj => obj.Address != 0
            && ((GameObject*)obj.Address)->EntityId == leaderId);
        if (leader == null || leader.Address == player.Address)
        {
            diagnostics.WriteThrottled("bmr-no-visible-leader", "BMR跟随",
                $"队长当前不可见或队长是本人；entityId={leaderId}。", TimeSpan.FromSeconds(10));
            SuspendDungeonFollow();
            return;
        }

        var gap = Vector3.Distance(player.Position, leader.Position);
        var distance = Math.Clamp(configuration.DungeonFollowDistance, 0f, 10f);
        var leaderContentId = group->PartyMembers[(int)group->PartyLeaderIndex].ContentId;
        if (externalPlugins.SetDungeonFollow(leaderContentId, leader.Name.TextValue, distance))
        {
            dungeonFollowByBossMod = true;
            externalPlugins.SetNavigating(true);
            StatusText = $"宝物库中 BMR 连续跟随队长 · {gap:F0}y（设定 {distance:F1}y）";
            return;
        }

        if (dungeonFollowByBossMod) SuspendDungeonFollow();
        StatusText = "BMR 跟随未就绪，请在关于页开启诊断模式查看原因";
    }

    private void StopDungeonFollow()
    {
        externalPlugins.StopDungeonFollow();
        if (dungeonFollowByBossMod) externalPlugins.SetNavigating(false);
        dungeonFollowByBossMod = false;
    }

    private void SuspendDungeonFollow()
    {
        externalPlugins.SuspendDungeonFollow();
        if (dungeonFollowByBossMod) externalPlugins.SetNavigating(false);
        dungeonFollowByBossMod = false;
    }

    private void SelectTarget(MapFlagTarget target, bool isManual)
    {
        activeTarget = target;
        manualSelection = isManual;
        destination = null;
        routePlan = null;
        ResetTeleportState();
        teleportedTargetSerial = null;
        routeComparedTargetSerial = null;
        recoveryAttempts = 0;
        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        ClearGameTarget();

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null
            && Plugin.ClientState.TerritoryType == target.TerritoryId
            && !(target.IsHunt && configuration.HuntAutoInstance && huntTargetInstance > 0
                && lifestream.GetNumberOfInstances() > 1
                && lifestream.GetCurrentInstance() != huntTargetInstance)
            && HorizontalDistance(player.Position, GetResolvedWorldPosition(target, player.Position.Y))
                <= ArrivalThreshold(target))
        {
            diagnostics.Write(
                "导航",
                $"新坐标已在到达范围内，不再上坐骑或重新寻路：sender={target.Sender}，map=({target.MapX:F1},{target.MapY:F1})。" );
            CompleteArrival();
            return;
        }

        if (paused)
        {
            SetState(AutomationState.Paused, $"已选择 {target.Sender} 的坐标，继续后导航");
        }
        else
        {
            SetState(AutomationState.WaitingForPlayer,
                isManual ? $"已手动选择 {target.Sender} 的坐标" : "正在准备最新坐标");
        }
    }

    private void ProcessPlayerReady()
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (Plugin.ObjectTable.LocalPlayer == null || IsLoadingOrOccupied())
        {
            StatusText = "读图或过场中，目的地已保留";
            return;
        }

        if (Plugin.ClientState.TerritoryType != activeTarget.TerritoryId)
        {
            if (ShouldCompareTeleportRoute())
            {
                var targetPosition = activeTarget.ToWorld(0f);
                var nearest = teleporter.GetCandidates(activeTarget.TerritoryId)
                    .MinBy(candidate => HorizontalDistance(candidate.Position, targetPosition));
                if (nearest == null)
                {
                    routeComparedTargetSerial = activeTarget.Serial;
                    StatusText = "目标位于其他地图，未找到已解锁的目标地图以太水晶";
                    diagnostics.Write("传送判断", $"目标位于 territory={activeTarget.TerritoryId}，但没有找到已解锁水晶，无法自动跨地图传送。");
                    return;
                }

                if (BeginTeleport(nearest))
                {
                    routeComparedTargetSerial = activeTarget.Serial;
                    StatusText = $"正在准备传送至 {nearest.Name}";
                    return;
                }

                StatusText = "暂时无法发起传送，正在等待重试";
                return;
            }

            StatusText = $"目标位于其他地图（Territory {activeTarget.TerritoryId}），等待进入该地图";
            return;
        }

        if (activeTarget.IsHunt && configuration.HuntAutoInstance && huntTargetInstance > 0)
        {
            if (!lifestream.IsInstalled)
            {
                StatusText = $"目标在 {huntTargetInstance} 线，等待 Lifestream 换线";
                return;
            }

            var count = lifestream.GetNumberOfInstances();
            if (count == 0)
            {
                StatusText = "正在确认当前地图的线路数量";
                return;
            }
            if (count > 1 && huntTargetInstance > count)
            {
                StatusText = $"目标 {huntTargetInstance} 线超出当前地图的 {count} 条线路";
                return;
            }
            if (count > 1 && lifestream.GetCurrentInstance() != huntTargetInstance)
            {
                if (DateTime.UtcNow - lastInstanceRequestUtc < TimeSpan.FromSeconds(35))
                {
                    StatusText = $"正在换到 {huntTargetInstance} 线";
                    return;
                }
                if (lifestream.ChangeInstance(huntTargetInstance))
                {
                    lastInstanceRequestUtc = DateTime.UtcNow;
                    StatusText = $"已请求换到 {huntTargetInstance} 线";
                    diagnostics.Write("狩猎换线", StatusText);
                    return;
                }

                var playerPosition = Plugin.ObjectTable.LocalPlayer!.Position;
                var crystal = teleporter.GetCandidates(activeTarget.TerritoryId)
                    .MinBy(candidate => HorizontalDistance(candidate.Position, playerPosition));
                if (crystal == null)
                {
                    StatusText = "当前地图无已解锁的大水晶，无法自动换线";
                    return;
                }
                if (DateTime.UtcNow - lastInstanceTeleportUtc >= TimeSpan.FromSeconds(35)
                    && !Plugin.Condition[ConditionFlag.InCombat])
                {
                    lastInstanceTeleportUtc = DateTime.UtcNow;
                    BeginTeleport(crystal);
                }
                else StatusText = $"正在等待靠近 {crystal.Name} 后换到 {huntTargetInstance} 线";
                return;
            }
        }

        if (!vnavmesh.IsInstalled)
        {
            Fail("vnavmesh 未安装或未加载");
            return;
        }

        SetMapFlag(activeTarget);
        destination = ResolveDestination(activeTarget);
        if (destination == null)
        {
            Fail("无法解析目的地");
            return;
        }

        if (ShouldCompareTeleportRoute())
        {
            if (!vnavmesh.IsReady())
            {
                SetState(AutomationState.WaitingForVnavmesh, "等待 vnavmesh 就绪后比较传送路线");
                return;
            }

            if (BeginRoutePlanning(recovery: false))
            {
                return;
            }
        }

        ContinueToMountOrNavigate();
    }

    private bool ShouldCompareTeleportRoute()
        => activeTarget != null
            && (activeTarget.IsHunt ? configuration.HuntAutoTeleport
                : true)
            && (activeTarget.IsHunt
                || configuration.OperatingMode == OperatingMode.Leader
                || configuration.AutoTeleport
                || configuration.AcceptPartyTeleportRequests
                    && (Plugin.ClientState.TerritoryType != activeTarget.TerritoryId
                        || activeTarget.IsCrossParty))
            && !Plugin.Condition[ConditionFlag.InCombat]
            && teleportedTargetSerial != activeTarget.Serial
            && routeComparedTargetSerial != activeTarget.Serial;

    private void ContinueToMountOrNavigate()
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (configuration.AutoMount && !IsMounted() && !Plugin.Condition[ConditionFlag.InCombat])
        {
            SetState(AutomationState.Mounting, "正在上坐骑");
        }
        else
        {
            SetState(AutomationState.WaitingForVnavmesh, "等待 vnavmesh 就绪");
        }
    }

    private unsafe void ProcessMounting(DateTime now)
    {
        if (IsMounted())
        {
            SetState(AutomationState.WaitingForVnavmesh, "等待 vnavmesh 就绪");
            return;
        }

        if (IsLoadingOrOccupied())
        {
            StatusText = "人物状态变化中，等待上坐骑";
            return;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            SetState(AutomationState.WaitingForVnavmesh, "战斗中改为步行导航");
            return;
        }

        if (now - lastMountAttemptUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        lastMountAttemptUtc = now;
        var actionManager = ActionManager.Instance();
        if (actionManager != null
            && actionManager->GetActionStatus(ActionType.GeneralAction, 9) == 0)
        {
            actionManager->UseAction(ActionType.GeneralAction, 9);
        }
    }

    private void ProcessVnavReady()
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (!vnavmesh.IsReady())
        {
            StatusText = "等待 vnavmesh 生成导航网格";
            return;
        }

        if (ShouldCompareTeleportRoute())
        {
            if (BeginRoutePlanning(recovery: false))
            {
                return;
            }
        }

        if (configuration.AutoMount && !IsMounted() && !Plugin.Condition[ConditionFlag.InCombat])
        {
            SetState(AutomationState.Mounting, "正在上坐骑");
            return;
        }

        if (!StartNavigation())
        {
            StatusText = "vnavmesh 暂时无法开始导航，正在等待重试";
        }
    }

    private void ProcessNavigation(DateTime now)
    {
        if (activeTarget == null || destination == null)
        {
            Fail("导航目标丢失");
            return;
        }

        if (Plugin.ClientState.TerritoryType != activeTarget.TerritoryId)
        {
            vnavmesh.Stop();
            externalPlugins.SetNavigating(false);
            destination = null;
            SetState(AutomationState.WaitingForPlayer, "地图发生变化，等待重新规划");
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return;
        }

        var distance = HorizontalDistance(player.Position, destination.Value);
        if (distance <= ArrivalThreshold(activeTarget))
        {
            vnavmesh.Stop();
            externalPlugins.SetNavigating(false);

            if (configuration.AutoDismount && IsMounted())
            {
                SetState(Plugin.Condition[ConditionFlag.InFlight]
                        ? AutomationState.Landing
                        : AutomationState.Dismounting,
                    Plugin.Condition[ConditionFlag.InFlight] ? "正在落地" : "正在下坐骑");
            }
            else
            {
                CompleteArrival();
            }

            return;
        }

        if (CheckForStuck(now, player.Position, distance))
        {
            return;
        }

        StatusText = $"前往 {activeTarget.PlaceName}  X {activeTarget.MapX:F1}  Y {activeTarget.MapY:F1} · {distance:F0}y";
        if (vnavmesh.IsBusy() || now - lastNavigationAttemptUtc < NavigationRetryInterval)
        {
            return;
        }

        if (!StartNavigation())
        {
            StatusText = $"路线中断，正在重新规划 · 距离 {distance:F0}y";
        }
    }

    private bool CheckForStuck(DateTime now, Vector3 position, float distance)
    {
        if (IsLoadingOrOccupied()
            || Plugin.Condition.Any(
                ConditionFlag.Jumping,
                ConditionFlag.Jumping61,
                ConditionFlag.MountOrOrnamentTransition))
        {
            ResetProgress(position, distance, now);
            return false;
        }

        if (HorizontalDistance(position, progressAnchor) >= ProgressDistance
            || progressDistance - distance >= ProgressDistance)
        {
            ResetProgress(position, distance, now);
            return false;
        }

        if ((now - lastProgressUtc).TotalSeconds < Math.Clamp(configuration.StuckSeconds, 1f, 30f))
        {
            return false;
        }

        if (recoveryAttempts == 0)
        {
            recoveryAttempts++;
            vnavmesh.Stop();
            ResetProgress(position, distance, now);
            StatusText = "检测到移动停滞，正在重新规划路线";
            _ = StartNavigation();
            return true;
        }

        if (configuration.TeleportWhenStuck
            && teleportedTargetSerial != activeTarget?.Serial
            && BeginRoutePlanning(recovery: true))
        {
            return true;
        }

        Fail("重新规划后仍无法前进，已停止等待人工处理");
        return true;
    }

    private unsafe void ProcessLanding(DateTime now)
    {
        if (!IsMounted())
        {
            CompleteArrival();
            return;
        }

        if (!Plugin.Condition[ConditionFlag.InFlight])
        {
            SetState(AutomationState.Dismounting, "已落地，正在下坐骑");
            return;
        }

        if (now - lastDismountAttemptUtc < ActionRetryInterval)
        {
            return;
        }

        lastDismountAttemptUtc = now;
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
    }

    private unsafe void ProcessDismounting(DateTime now)
    {
        if (!IsMounted())
        {
            CompleteArrival();
            return;
        }

        if (Plugin.Condition.Any(
                ConditionFlag.Jumping,
                ConditionFlag.Jumping61,
                ConditionFlag.MountOrOrnamentTransition))
        {
            return;
        }

        if (Plugin.Condition[ConditionFlag.InFlight])
        {
            SetState(AutomationState.Landing, "正在落地");
            return;
        }

        if (now - lastDismountAttemptUtc < ActionRetryInterval)
        {
            return;
        }

        lastDismountAttemptUtc = now;
        var actionManager = ActionManager.Instance();
        if (actionManager->GetActionStatus(ActionType.Mount, 0) == 0)
        {
            actionManager->UseAction(ActionType.Mount, 0);
        }
        else
        {
            actionManager->UseAction(ActionType.GeneralAction, 23);
        }
    }

    private bool StartNavigation()
    {
        if (activeTarget == null || Plugin.ObjectTable.LocalPlayer == null)
        {
            return false;
        }

        destination ??= ResolveDestination(activeTarget);
        if (destination == null)
        {
            return false;
        }

        SetMapFlag(activeTarget);
        var fly = configuration.UseFlight && IsMounted();
        var navigationRange = Math.Clamp(configuration.ArrivalTolerance / 2f, 0.25f, 8f);
        var started = vnavmesh.MoveCloseTo(
            destination.Value,
            fly,
            navigationRange);

        lastNavigationAttemptUtc = DateTime.UtcNow;
        if (!started)
        {
            diagnostics.Write(
                "导航",
                $"vnavmesh 拒绝开始导航：destination=({destination.Value.X:F1},{destination.Value.Y:F1},{destination.Value.Z:F1})，fly={fly}，range={navigationRange:F1}；{BuildDiagnosticContext()}");
            return false;
        }

        externalPlugins.SetNavigating(true);
        var player = Plugin.ObjectTable.LocalPlayer.Position;
        ResetProgress(player, HorizontalDistance(player, destination.Value), DateTime.UtcNow);
        diagnostics.Write(
            "导航",
            $"已开始{(fly ? "飞行" : "步行")}导航：destination=({destination.Value.X:F1},{destination.Value.Y:F1},{destination.Value.Z:F1})；{BuildDiagnosticContext()}");
        SetState(AutomationState.Navigating,
            $"开始{(fly ? "飞行" : "步行")}导航：{activeTarget.MapX:F1}, {activeTarget.MapY:F1}");
        return true;
    }

    private bool BeginRoutePlanning(bool recovery)
    {
        if (activeTarget == null || destination == null || Plugin.ObjectTable.LocalPlayer == null || !vnavmesh.IsReady())
        {
            return false;
        }

        var candidates = teleporter.GetCandidates(activeTarget.TerritoryId);
        if (candidates.Count == 0)
        {
            diagnostics.Write("路线判断", $"目标 serial={activeTarget.Serial} 没有可比较的以太水晶，继续直接导航。");
            return false;
        }

        var fly = configuration.UseFlight;
        var directTask = vnavmesh.Pathfind(Plugin.ObjectTable.LocalPlayer.Position, destination.Value, fly);
        var candidatePaths = candidates
            .Select(candidate =>
            {
                var origin = vnavmesh.NearestPoint(candidate.Position) ?? candidate.Position;
                return new CandidatePath(candidate, origin, vnavmesh.Pathfind(origin, destination.Value, fly));
            })
            .ToList();

        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        routePlan = new RoutePlan(
            activeTarget.Serial,
            recovery,
            destination.Value,
            DateTime.UtcNow,
            directTask,
            candidatePaths);
        routeComparedTargetSerial = activeTarget.Serial;
        SetState(AutomationState.PlanningRoute,
            recovery ? "重新寻路仍停滞，正在计算以太水晶方案" : "正在比较直达与传送路线");
        return true;
    }

    private void ProcessRoutePlanning()
    {
        if (routePlan == null || activeTarget == null || routePlan.TargetSerial != activeTarget.Serial)
        {
            routePlan = null;
            SetState(AutomationState.WaitingForPlayer, "目的地已变化，重新规划");
            return;
        }

        var tasks = routePlan.CandidatePaths
            .Where(path => path.Task != null)
            .Select(path => path.Task!)
            .ToList();
        var allTasksCompleted = (routePlan.DirectTask == null || routePlan.DirectTask.IsCompleted)
            && tasks.All(task => task.IsCompleted);
        if (!allTasksCompleted && DateTime.UtcNow - routePlan.StartedUtc < RoutePlanningTimeout)
        {
            return;
        }

        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? routePlan.Destination;
        var directMeasurement = MeasurePath(routePlan.DirectTask, playerPosition, routePlan.Destination);
        var directLength = directMeasurement.Length;
        if (!directMeasurement.IsValid)
        {
            directLength = HorizontalDistance(playerPosition, routePlan.Destination);
            diagnostics.Write(
                "路线判断",
                $"直达路线无效，改用直线距离 {directLength:F1}：{directMeasurement.Reason}。");
        }

        CandidatePath? best = null;
        var bestLength = float.PositiveInfinity;
        foreach (var candidate in routePlan.CandidatePaths)
        {
            var measurement = MeasurePath(candidate.Task, candidate.Origin, routePlan.Destination);
            var length = measurement.Length;
            if (!measurement.IsValid)
            {
                length = HorizontalDistance(candidate.Origin, routePlan.Destination);
            }

            diagnostics.Write(
                "路线判断",
                $"候选 {candidate.Candidate.Name}#{candidate.Candidate.Id}：origin={FormatPoint(candidate.Origin)}，crystal={FormatPoint(candidate.Candidate.Position)}，path={(measurement.IsValid ? measurement.Length.ToString("F1") : "无效")}，fallback={length:F1}，endpoint={FormatPoint(measurement.Endpoint)}，reason={measurement.Reason}。");

            if (length < bestLength)
            {
                best = candidate;
                bestLength = length;
            }
        }

        var alreadyNearBestCrystal = best != null
            && Plugin.ClientState.TerritoryType == activeTarget.TerritoryId
            && HorizontalDistance(playerPosition, best.Candidate.Position) <= 80f;
        var shouldTeleport = best != null
            && float.IsFinite(bestLength)
            && !alreadyNearBestCrystal
            && (routePlan.Recovery || bestLength < directLength);

        Plugin.Log.Information(
            "Route comparison for target {TargetSerial}: direct={DirectLength:F1}, bestAetheryte={AetheryteName}, best={BestLength:F1}, teleport={ShouldTeleport}.",
            activeTarget.Serial,
            directLength,
            best?.Candidate.Name ?? "none",
            bestLength,
            shouldTeleport);
        diagnostics.Write(
            "路线判断",
            $"serial={activeTarget.Serial}，direct={directLength:F1}，best={best?.Candidate.Name ?? "无"}#{best?.Candidate.Id ?? 0}，aetheryteRoute={bestLength:F1}，nearCrystal={alreadyNearBestCrystal}，recovery={routePlan.Recovery}，teleport={shouldTeleport}。");

        routePlan = null;
        if (shouldTeleport && best != null && BeginTeleport(best.Candidate))
        {
            return;
        }

        ContinueToMountOrNavigate();
    }

    private unsafe void ProcessTeleporting(DateTime now)
    {
        if (activeTarget == null)
        {
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (teleportIssuedUtc == DateTime.MinValue
            && now - teleportPreparedUtc >= TeleportPrepareTimeout)
        {
            Plugin.Log.Warning("Teleport preparation timed out; continuing by navigation.");
            diagnostics.Write("传送", $"准备传送超时：{BuildDiagnosticContext()}；恢复普通导航。");
            ResetTeleportState();
            ContinueAfterTeleportFailure();
            return;
        }

        externalPlugins.SetNavigating(false);
        if (Plugin.Condition.Any(ConditionFlag.Casting, ConditionFlag.Casting87))
        {
            teleportSawCasting = true;
            StatusText = "传送施法中";
            return;
        }

        if (vnavmesh.IsBusy())
        {
            vnavmesh.Stop();
            StatusText = "正在停止导航，等待人物静止";
            return;
        }

        if (IsLoadingOrOccupied())
        {
            teleportSawLoading = true;
            StatusText = "传送读图中";
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        var arrivedNearCrystal = player != null
            && teleportArrivalPosition != null
            && HorizontalDistance(player.Position, teleportArrivalPosition.Value) < 150f;
        var movedSinceRequest = player != null
            && teleportOriginPosition != null
            && HorizontalDistance(player.Position, teleportOriginPosition.Value) >= 3f;
        var arrivedInTargetTerritory = Plugin.ClientState.TerritoryType == activeTarget.TerritoryId;

        if (teleportSawLoading
            && arrivedInTargetTerritory
            && (teleportOriginTerritoryId != Plugin.ClientState.TerritoryType
                || movedSinceRequest
                || arrivedNearCrystal))
        {
            teleportedTargetSerial = activeTarget.Serial;
            destination = null;
            ResetTeleportState();
            SetState(AutomationState.WaitingForPlayer, "传送完成，重新规划前往坐标");
            return;
        }

        if (teleportIssuedUtc == DateTime.MinValue)
        {
            if (Plugin.Condition[ConditionFlag.InCombat])
            {
                Plugin.Log.Information("Teleport preparation was interrupted by combat; continuing by navigation.");
                diagnostics.Write("传送", "准备阶段进入战斗，取消本次传送并恢复普通导航。");
                ResetTeleportState();
                ContinueAfterTeleportFailure();
                return;
            }

            if (Plugin.Condition.Any(
                    ConditionFlag.BeingMoved,
                    ConditionFlag.Jumping,
                    ConditionFlag.Jumping61,
                    ConditionFlag.MountOrOrnamentTransition))
            {
                vnavmesh.Stop();
                teleportStationarySinceUtc = DateTime.MinValue;
                StatusText = "等待人物完全静止后传送";
                diagnostics.WriteThrottled(
                    "teleport-moving",
                    "传送",
                    $"尚未静止，继续等待：{BuildDiagnosticContext()}",
                    TimeSpan.FromSeconds(2));
                return;
            }

            if (teleportStationarySinceUtc == DateTime.MinValue)
            {
                teleportStationarySinceUtc = now;
                StatusText = "已停止移动，正在确认传送条件";
                return;
            }

            if (now - teleportStationarySinceUtc < TeleportPrepareDelay)
            {
                StatusText = "正在确认人物保持静止";
                return;
            }

            if (!TeleportService.CanTeleportNow())
            {
                StatusText = "等待传送技能可用";
                diagnostics.WriteThrottled(
                    "teleport-action-unavailable",
                    "传送",
                    $"传送技能当前不可用：{BuildDiagnosticContext()}",
                    TimeSpan.FromSeconds(2));
                return;
            }

            if (teleportAetheryteId is not { } aetheryteId || !teleporter.Teleport(aetheryteId))
            {
                Plugin.Log.Warning("Teleport request could not be submitted; continuing by navigation.");
                diagnostics.Write("传送", $"提交传送请求失败，aetheryte={teleportAetheryteId?.ToString() ?? "null"}；恢复普通导航。");
                ResetTeleportState();
                ContinueAfterTeleportFailure();
                return;
            }

            teleportAttemptCount++;
            teleportIssuedUtc = now;
            teleportSawCasting = false;
            teleportSawLoading = false;
            SetState(
                AutomationState.Teleporting,
                teleportAttemptCount == 1
                    ? $"正在传送至 {teleportAetheryteName}"
                    : $"正在重试传送至 {teleportAetheryteName}");
            return;
        }

        var elapsed = now - teleportIssuedUtc;
        var requestStalled = !teleportSawLoading
            && ((!teleportSawCasting && elapsed >= TeleportStartTimeout)
                || (teleportSawCasting && elapsed >= TeleportCancelledTimeout));
        if (requestStalled)
        {
            if (teleportAttemptCount < MaxTeleportAttempts)
            {
                Plugin.Log.Warning(
                    "Teleport to {AetheryteName} did not start; retrying ({Attempt}/{MaxAttempts}).",
                    teleportAetheryteName,
                    teleportAttemptCount + 1,
                    MaxTeleportAttempts);
                PrepareForTeleportAttempt();
                teleportPreparedUtc = now;
                teleportStationarySinceUtc = DateTime.MinValue;
                teleportIssuedUtc = DateTime.MinValue;
                teleportSawCasting = false;
                teleportSawLoading = false;
                StatusText = $"传送未开始，准备重试 {teleportAetheryteName}";
                return;
            }

            Plugin.Log.Warning(
                "Teleport to {AetheryteName} did not start after {AttemptCount} attempts; continuing by navigation.",
                teleportAetheryteName,
                teleportAttemptCount);
            diagnostics.Write("传送", $"连续 {teleportAttemptCount} 次未观察到有效施法或读图，恢复普通导航。{BuildDiagnosticContext()}");
            ResetTeleportState();
            ContinueAfterTeleportFailure();
            return;
        }

        if (elapsed >= TeleportConfirmationTimeout)
        {
            Plugin.Log.Warning(
                "Teleport was not confirmed after {AttemptCount} attempt(s); continuing without teleport.",
                teleportAttemptCount);
            diagnostics.Write("传送", $"传送确认超时，恢复普通导航。{BuildDiagnosticContext()}");
            ResetTeleportState();
            ContinueAfterTeleportFailure();
        }
    }

    private void ContinueAfterTeleportFailure()
    {
        if (activeTarget != null && Plugin.ClientState.TerritoryType != activeTarget.TerritoryId)
        {
            routeComparedTargetSerial = activeTarget.Serial;
            SetState(AutomationState.WaitingForPlayer, "自动传送未完成，等待进入目标地图");
            return;
        }

        ContinueToMountOrNavigate();
    }

    private void ProcessPartyTeleport(DateTime now)
    {
        if (activeTarget == null)
        {
            ResetPartyTeleportState();
            SetState(AutomationState.Idle, "等待小队坐标");
            return;
        }

        if (Plugin.Condition[ConditionFlag.Casting])
        {
            StatusText = "队友传送施法中";
            return;
        }

        if (IsLoadingOrOccupied())
        {
            partyTeleportSawLoading = true;
            StatusText = "队友传送读图中";
            return;
        }

        var timedOut = now - partyTeleportAcceptedUtc!.Value >= PartyTeleportTimeout;
        if (!partyTeleportSawLoading && !timedOut)
        {
            return;
        }

        destination = null;
        routePlan = null;
        ResetPartyTeleportState();
        SetState(
            AutomationState.WaitingForPlayer,
            timedOut ? "队友传送未发生，重新寻路" : "队友传送完成，重新寻路");
    }

    private bool BeginTeleport(AetheryteCandidate candidate)
    {
        PrepareForTeleportAttempt();
        teleportAetheryteId = candidate.Id;
        teleportAetheryteName = candidate.Name;
        teleportArrivalPosition = candidate.Position;
        teleportOriginTerritoryId = Plugin.ClientState.TerritoryType;
        teleportOriginPosition = Plugin.ObjectTable.LocalPlayer?.Position;
        teleportAttemptCount = 0;
        teleportPreparedUtc = DateTime.UtcNow;
        teleportStationarySinceUtc = DateTime.MinValue;
        teleportIssuedUtc = DateTime.MinValue;
        teleportSawCasting = false;
        teleportSawLoading = false;
        diagnostics.Write(
            "传送",
            $"开始准备传送至 {candidate.Name}#{candidate.Id}，水晶位置=({candidate.Position.X:F1},{candidate.Position.Y:F1},{candidate.Position.Z:F1})；{BuildDiagnosticContext()}");
        SetState(AutomationState.Teleporting, $"正在停止导航，准备传送至 {candidate.Name}");
        return true;
    }

    private void PrepareForTeleportAttempt()
    {
        // Teleport casts are cancelled by movement. Always tear down navigation before
        // issuing or retrying a teleport, regardless of the state that called us.
        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        destination = null;
        routePlan = null;
    }

    private void ResetTeleportState()
    {
        teleportAetheryteId = null;
        teleportAetheryteName = null;
        teleportArrivalPosition = null;
        teleportOriginTerritoryId = 0;
        teleportOriginPosition = null;
        teleportAttemptCount = 0;
        teleportPreparedUtc = DateTime.MinValue;
        teleportStationarySinceUtc = DateTime.MinValue;
        teleportIssuedUtc = DateTime.MinValue;
        teleportSawCasting = false;
        teleportSawLoading = false;
    }

    private void CompleteArrival()
    {
        var completed = activeTarget;
        if (completed != null)
        {
            var keys = destinations
                .Where(pair => IsNearby(completed, pair.Value))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in keys)
            {
                destinations.Remove(key);
            }

            Plugin.Log.Information(
                "Arrived at destination {X:F1}, {Y:F1}; removed {Count} nearby destination(s).",
                completed.MapX,
                completed.MapY,
                keys.Count);
        }

        activeTarget = null;
        destination = null;
        routePlan = null;
        ResetTeleportState();
        ResetPartyTeleportState();
        manualSelection = false;
        recoveryAttempts = 0;
        routeComparedTargetSerial = null;
        externalPlugins.SetNavigating(false);
        SetState(AutomationState.Idle, "已到达，等待挖宝、战斗或新的小队坐标");
        if (completed != null)
        {
            DestinationReached?.Invoke(completed);
        }
    }

    private void Fail(string reason)
    {
        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        Plugin.Log.Error("{Reason}", reason);
        activeTarget = null;
        destination = null;
        routePlan = null;
        ResetTeleportState();
        ResetPartyTeleportState();
        manualSelection = false;
        routeComparedTargetSerial = null;
        SetState(AutomationState.Error, reason);
    }

    private void ResetNavigation(bool clearDestinations)
    {
        StopDungeonFollow();
        vnavmesh.Stop();
        externalPlugins.SetNavigating(false);
        activeTarget = null;
        huntTargetInstance = 0;
        huntCombatNavigation = false;
        destination = null;
        routePlan = null;
        ResetTeleportState();
        ResetPartyTeleportState();
        manualSelection = false;
        recoveryAttempts = 0;
        routeComparedTargetSerial = null;
        ClearGameTarget();
        if (clearDestinations)
        {
            destinations.Clear();
        }
    }

    private static unsafe SenderIdentity ResolveSender(SeString sender)
    {
        var source = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        var name = source?.PlayerName ?? sender.TextValue;
        var worldId = source?.World.RowId ?? 0;
        ulong contentId = 0;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "本地测试";
        }

        var groupManager = GroupManager.Instance();
        var group = groupManager == null ? null : groupManager->GetGroup();
        if (group != null)
        {
            for (var i = 0; i < group->MemberCount; ++i)
            {
                var member = group->PartyMembers[i];
                if (member.HomeWorld == worldId && member.NameString == name)
                {
                    contentId = member.ContentId;
                    break;
                }
            }
        }

        return new SenderIdentity(name, worldId, contentId);
    }

    private static bool IsSameSender(MapFlagTarget first, MapFlagTarget second)
    {
        if (first.SenderContentId != 0 && second.SenderContentId != 0)
        {
            return first.SenderContentId == second.SenderContentId;
        }

        if (!string.Equals(first.SenderName.Trim(), second.SenderName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return first.SenderWorldId == 0
            || second.SenderWorldId == 0
            || first.SenderWorldId == second.SenderWorldId;
    }

    private Vector3? ResolveDestination(MapFlagTarget target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            return null;
        }

        if (target.IsHunt)
        {
            SetMapFlag(target);
            var raw = target.ToWorld(player.Position.Y);
            return vnavmesh.ResolveFlagPoint() ?? vnavmesh.NearestPoint(raw) ?? raw;
        }

        if (treasureSpots.TryResolve(target, out var treasureSpot, out var snapDistance))
        {
            var mesh = vnavmesh.NearestPoint(treasureSpot, 6f, 4f);
            // Mesh queries can move a spot to a different floor or several yalms sideways.
            var resolved = mesh is { } point
                && HorizontalDistance(point, treasureSpot) <= 1.5f
                && MathF.Abs(point.Y - treasureSpot.Y) <= 2f
                    ? point
                    : treasureSpot;
            diagnostics.WriteThrottled(
                $"treasure-spot-{target.Serial}",
                "藏宝点",
                $"旗标已校正到真实挖掘点：偏差 {snapDistance:F1}y，raw={FormatPoint(target.ToWorld(0f))}，resolved={FormatPoint(resolved)}。",
                TimeSpan.FromMinutes(1));
            return resolved;
        }

        var correction = treasureSpots.Inspect(target);
        diagnostics.Write("藏宝点", $"旗标未校正：{correction.Reason}；territory={target.TerritoryId}，map={target.MapId}，raw={FormatPoint(target.ToWorld(0f))}。");

        SetMapFlag(target);
        var fallback = target.ToWorld(player.Position.Y);
        return vnavmesh.ResolveFlagPoint() ?? vnavmesh.NearestPoint(fallback) ?? fallback;
    }

    private static PathMeasurement MeasurePath(Task<List<Vector3>>? task, Vector3 origin, Vector3 destination)
    {
        if (task == null)
        {
            return new(float.PositiveInfinity, null, false, "未创建路线任务");
        }

        if (!task.IsCompletedSuccessfully)
        {
            return new(float.PositiveInfinity, null, false,
                task.IsCompleted ? $"路线任务状态 {task.Status}" : "路线计算超时");
        }

        if (task.Result.Count == 0)
        {
            return new(float.PositiveInfinity, null, false, "路线没有返回路径点");
        }

        var length = 0f;
        var previous = origin;
        foreach (var waypoint in task.Result)
        {
            length += Vector3.Distance(previous, waypoint);
            previous = waypoint;
        }

        var endpointDistance = HorizontalDistance(previous, destination);
        if (endpointDistance > 30f)
        {
            return new(length, previous, false, $"末端距目的地 {endpointDistance:F1}y");
        }

        var straightDistance = HorizontalDistance(origin, destination);
        if (length + endpointDistance + 2f < straightDistance)
        {
            return new(length, previous, false,
                $"路线长度 {length:F1}y 小于可达下限 {straightDistance - endpointDistance:F1}y");
        }

        return new(length, previous, true, $"有效，末端误差 {endpointDistance:F1}y");
    }

    private static string FormatPoint(Vector3? point)
        => point == null ? "无" : $"({point.Value.X:F1},{point.Value.Y:F1},{point.Value.Z:F1})";

    private static bool IsNearby(MapFlagTarget left, MapFlagTarget right)
        => left.TerritoryId == right.TerritoryId
            && left.MapId == right.MapId
            && Math.Abs(left.MapX - right.MapX) <= NearbyMapCoordinateTolerance
            && Math.Abs(left.MapY - right.MapY) <= NearbyMapCoordinateTolerance;

    private static bool IsMounted()
        => Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion];

    private static bool IsLoadingOrOccupied()
        => Plugin.Condition.Any(
            ConditionFlag.BetweenAreas,
            ConditionFlag.BetweenAreas51,
            ConditionFlag.OccupiedInCutSceneEvent,
            ConditionFlag.WatchingCutscene,
            ConditionFlag.WatchingCutscene78,
            ConditionFlag.LoggingOut);

    private float ArrivalThreshold(MapFlagTarget? target = null)
    {
        var tolerance = Math.Clamp(configuration.ArrivalTolerance, 0f, 30f);
        if (target?.IsOwnTreasure == true && treasureSpots.TryResolve(target, out _, out _))
        {
            tolerance = Math.Min(tolerance, 2.5f);
        }

        return Math.Max(tolerance, 0.25f);
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static void ClearGameTarget()
    {
        Plugin.TargetManager.Target = null;
        Plugin.TargetManager.SoftTarget = null;
    }

    private static unsafe void SetMapFlag(MapFlagTarget target)
    {
        try
        {
            var agent = AgentMap.Instance();
            if (agent == null)
            {
                return;
            }

            agent->FlagMarkerCount = 0;
            agent->SetFlagMapMarker(
                target.TerritoryId,
                target.MapId,
                new Vector3(target.RawX / 1000f, 0f, target.RawY / 1000f));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to set the map flag for target {Serial}.", target.Serial);
        }
    }

    private void ResetProgress(Vector3 position, float distance, DateTime now)
    {
        progressAnchor = position;
        progressDistance = distance;
        lastProgressUtc = now;
    }

    private void ResetPartyTeleportState()
    {
        partyTeleportAcceptedUtc = null;
        partyTeleportSawLoading = false;
    }

    private void SetState(AutomationState state, string status)
    {
        var previousState = State;
        var previousStatus = StatusText;
        State = state;
        StatusText = status;
        if (previousState != state || !string.Equals(previousStatus, status, StringComparison.Ordinal))
        {
            diagnostics.Write("状态", $"{previousState} -> {state}；{status}；{BuildDiagnosticContext()}");
        }
    }

    private static string BuildDiagnosticContext()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var position = player == null
            ? "无"
            : $"({player.Position.X:F1},{player.Position.Y:F1},{player.Position.Z:F1})";
        return $"territory={Plugin.ClientState.TerritoryType}，position={position}，mounted={IsMounted()}，inFlight={Plugin.Condition[ConditionFlag.InFlight]}，beingMoved={Plugin.Condition[ConditionFlag.BeingMoved]}，combat={Plugin.Condition[ConditionFlag.InCombat]}";
    }

    private sealed record SenderIdentity(string Name, uint WorldId, ulong ContentId);

    private sealed record CandidatePath(
        AetheryteCandidate Candidate,
        Vector3 Origin,
        Task<List<Vector3>>? Task);

    private sealed record RoutePlan(
        long TargetSerial,
        bool Recovery,
        Vector3 Destination,
        DateTime StartedUtc,
        Task<List<Vector3>>? DirectTask,
        IReadOnlyList<CandidatePath> CandidatePaths);

    private sealed record PathMeasurement(
        float Length,
        Vector3? Endpoint,
        bool IsValid,
        string Reason);
}
