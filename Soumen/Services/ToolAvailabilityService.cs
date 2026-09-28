namespace Soumen.Services;

internal enum ToolEntryStatus
{
    Available,
    Unavailable,
    Occupied,
    NoHook,
}

/// <summary>Read-only compatibility check; independent of tool switches and installed Hooks.</summary>
internal sealed class ToolAvailabilityService
{
    private static readonly (string Id, string Guard, string[] Addresses)[] FixedEntries =
    [
        (nameof(Configuration.ToolSpeedEnabled), "MySpeedHook", ["_speedUpdateHook"]),
        (nameof(Configuration.ToolMaxAcceleration), "MySpeedHook", ["_speed2"]),
        (nameof(Configuration.ToolForceMovement), "MovePermission", ["_MovePermissionHook"]),
        (nameof(Configuration.ToolVerticalMovement), "YMove", ["_SendNormalMoveHook", "_SendCombatMoveHook"]),
        (nameof(Configuration.ToolAntiKnockback), "AntiKnock", ["_AntiKnockHook"]),
        (nameof(Configuration.ToolNoFallDamage), "NoFallDamage", ["_NoFallDamageHook"]),
        (nameof(Configuration.ToolNoDrop), "FallCheck", ["_FallCheckHook"]),
        (nameof(Configuration.ToolIgnoreCharm), "StatusCheck", ["noBewitchActionHook"]),
        (nameof(Configuration.ToolStatusBlock), "StatusCheck", ["_StatusCheckHook", "_ProcessPacketStatusEffectHookGL"]),
        (nameof(Configuration.ToolTargetRadiusEnabled), "ActorRadiusHook", ["_ActorRadiusHook"]),
        (nameof(Configuration.NoBackswingMovement), "NoBackswingHook", ["_NoBackswingHook"]),
        (nameof(Configuration.ToolNoActionMove), "NoActionMoveHook", ["_NoActionMoveHook"]),
        (nameof(Configuration.CancelFishingAnimation), "AutoCancelFSHAnimationHook", ["_getResourceSyncHook", "_getResourceAsyncHook"]),
        (nameof(Configuration.CancelGatheringAnimation), "AutoCancelFSHAnimationHook", ["_getResourceSyncHook", "_getResourceAsyncHook"]),
    ];

    private readonly Dictionary<string, ToolEntryStatus> snapshot = new(StringComparer.Ordinal);
    private DateTime expiresUtc;

    public ToolEntryStatus Check(string id)
    {
        if (DateTime.UtcNow >= expiresUtc) Refresh();
        return snapshot.GetValueOrDefault(id, ToolEntryStatus.Unavailable);
    }

    private void Refresh()
    {
        foreach (var (id, guard, addresses) in FixedEntries)
            snapshot[id] = Status(addresses.All(ToolHookAddresses.IsAvailable), guard);

        snapshot[nameof(Configuration.ToolActionRangeEnabled)] = Status(
            ToolHookAddresses.IsAvailable("_ActionRangeHook") && ToolCombatService.IsActionInRangeEntryAvailable(),
            "ActionRangeHook");
        snapshot[nameof(Configuration.ToolMovingCast)] =
            Status(ToolMovingCastService.IsEntryAvailable(), "NetRe");
        snapshot[nameof(Configuration.ToolCastReduction)] =
            Status(ToolCastRecastService.IsCastEntryAvailable(), "CastHook");
        var recast = Status(ToolCastRecastService.IsRecastEntryAvailable(), "RecastHook");
        snapshot[nameof(Configuration.ToolRecastReduction)] = recast;
        snapshot[nameof(Configuration.ToolRapidMudra)] = recast;

        // This overlay uses Dalamud's UI draw callback; it does not install a native Hook.
        snapshot[nameof(Configuration.FrontlineRadarEnabled)] = ToolEntryStatus.NoHook;
        expiresUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
    }

    private static ToolEntryStatus Status(bool compatible, string guard)
        => !compatible ? ToolEntryStatus.Unavailable
            : ExternalHookGuard.HasConflict(guard) ? ToolEntryStatus.Occupied
            : ToolEntryStatus.Available;
}
