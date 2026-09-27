using System.Reflection;
using Soumen.Models;

namespace Soumen.Services;

public sealed class ExternalPluginCoordinator
{
    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private bool? aeTargetingEnabled;
    private bool bossModArmed;
    private bool lazyLootArmed;
    private bool dungeonFollowArmed;
    private int dungeonFollowSlot = -1;
    private float dungeonFollowDistance = float.NaN;
    private float? dungeonFollowOriginalDistance;
    private int? dungeonFollowOriginalSlot;
    private bool? dungeonFollowOriginalTarget;
    private DateTime nextBossModSettingsReadUtc = DateTime.MinValue;
    private DateTime nextDungeonFollowAttemptUtc = DateTime.MinValue;
    private LazyLootRollMode? appliedLazyLootRollMode;

    public ExternalPluginCoordinator(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
    }

    public bool AeAssistInstalled => IsPluginLoaded("AEAssistV3");

    public bool BossModRebornInstalled => IsPluginLoaded("BossModReborn");

    public bool LazyLootInstalled => IsPluginLoaded("LazyLoot");

    public bool GlobetrotterInstalled => IsPluginLoaded("Globetrotter");

    public bool DailyRoutinesInstalled => IsPluginLoaded("DailyRoutines");

    public void StartRuntime()
    {
        if (!configuration.HuntEnabled && configuration.EnableBossModRebornIntegration && BossModRebornInstalled && !bossModArmed)
        {
            StartBossMod();
        }

        if (!configuration.HuntEnabled) ApplyLazyLootRollMode();

        SetNavigating(false);
    }

    public void StopRuntime()
    {
        StopDungeonFollow();
        SetNavigating(false);
        if (bossModArmed && BossModRebornInstalled)
        {
            StopBossMod();
        }

        if (lazyLootArmed && LazyLootInstalled)
        {
            Execute("/fulf off");
        }

        bossModArmed = false;
        lazyLootArmed = false;
        appliedLazyLootRollMode = null;
        aeTargetingEnabled = null;
    }

    public void RefreshRuntime()
    {
        if (!BossModRebornInstalled)
        {
            bossModArmed = false;
            dungeonFollowArmed = false;
            dungeonFollowSlot = -1;
            dungeonFollowOriginalDistance = null;
            dungeonFollowOriginalSlot = null;
            dungeonFollowOriginalTarget = null;
            nextBossModSettingsReadUtc = DateTime.MinValue;
            nextDungeonFollowAttemptUtc = DateTime.MinValue;
        }

        if (!configuration.HuntEnabled && configuration.EnableBossModRebornIntegration && BossModRebornInstalled && !bossModArmed)
        {
            StartBossMod();
        }
        else if ((configuration.HuntEnabled || !configuration.EnableBossModRebornIntegration) && bossModArmed && BossModRebornInstalled)
        {
            StopDungeonFollow();
            StopBossMod();
            bossModArmed = false;
        }

        if (!AeAssistInstalled)
        {
            aeTargetingEnabled = null;
        }

        if (!LazyLootInstalled)
        {
            lazyLootArmed = false;
            appliedLazyLootRollMode = null;
        }
        else if (!lazyLootArmed && !configuration.HuntEnabled)
        {
            ApplyLazyLootRollMode();
        }
    }

    public bool SetDungeonFollow(ulong leaderContentId, string leaderName, float distance)
    {
        if (!configuration.EnableBossModRebornIntegration || !BossModRebornInstalled || !bossModArmed
            || leaderContentId == 0)
        {
            diagnostics.WriteThrottled("bmr-unavailable", "BMR跟随",
                "未启用 BMR AI、BMR 未加载或队长 ContentId 不可用，不启动宝物库跟随。", TimeSpan.FromSeconds(10));
            return false;
        }
        if (DateTime.UtcNow < nextDungeonFollowAttemptUtc) return false;
        if (!TryGetBossModPartySlot(leaderContentId, out var slot))
        {
            nextDungeonFollowAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            diagnostics.Write("BMR跟随", $"未能把队长 {leaderName}（{leaderContentId}）匹配到 BMR 队伍槽位，五秒后重试。");
            return false;
        }
        if (dungeonFollowArmed && dungeonFollowSlot != slot) StopDungeonFollow();
        if (!dungeonFollowArmed)
        {
            if (!TryCaptureBossModFollowSettings()) return false;
            if (!Execute($"/bmrai follow slot{slot + 1}"))
            {
                nextDungeonFollowAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                diagnostics.Write("BMR跟随", "BMR 未注册 /bmrai 命令，五秒后重试。");
                return false;
            }
            // BMR's slot-follow mode follows the party member without targeting them.
            Execute("/bmrai followtarget off");
            Execute($"/bmrai maxdistanceslot {distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
            Execute("/bmrai followoutofcombat on");
            if ((!TryReadBossModFollowState(out var currentOoc, out var currentTarget)
                    || !currentOoc || currentTarget)
                && !TrySetBossModFollowSettings(true, false, distance))
            {
                dungeonFollowArmed = true;
                StopDungeonFollow();
                nextDungeonFollowAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                diagnostics.Write("BMR跟随", "BMR 脱战跟随设置未生效，五秒后重试。");
                return false;
            }
            if (!TryReadBossModFollowState(out var followOutOfCombat, out var followTarget)
                || !followOutOfCombat || followTarget)
            {
                dungeonFollowArmed = true;
                StopDungeonFollow();
                nextDungeonFollowAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                diagnostics.Write("BMR跟随", "BMR 未开启队友槽位脱战跟随，五秒后重试。");
                return false;
            }
            dungeonFollowSlot = slot;
            dungeonFollowArmed = true;
            dungeonFollowDistance = distance;
            nextDungeonFollowAttemptUtc = DateTime.MinValue;
            diagnostics.Write("BMR跟随", $"BMR 已选队长 {leaderName}：槽位 {slot + 1}，脱战跟随开启，距离 {distance:F1}y。");
        }
        if (float.IsNaN(dungeonFollowDistance) || Math.Abs(dungeonFollowDistance - distance) > 0.05f)
        {
            Execute($"/bmrai maxdistanceslot {distance.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
            if (TrySetBossModFollowSettings(true, false, distance))
            {
                dungeonFollowDistance = distance;
                diagnostics.Write("BMR跟随", $"队友槽位跟随距离设为 {distance:F1}y。");
            }
        }
        return true;
    }

    public void StopDungeonFollow()
    {
        if (!dungeonFollowArmed) return;
        dungeonFollowArmed = false;
        dungeonFollowSlot = -1;
        dungeonFollowDistance = float.NaN;
        var originalDistance = dungeonFollowOriginalDistance;
        var originalSlot = dungeonFollowOriginalSlot;
        var originalTarget = dungeonFollowOriginalTarget;
        dungeonFollowOriginalDistance = null;
        dungeonFollowOriginalSlot = null;
        dungeonFollowOriginalTarget = null;
        if (!BossModRebornInstalled) return;
        Execute("/bmrai followoutofcombat off");
        Execute($"/bmrai followtarget {(originalTarget == true ? "on" : "off")}");
        if (originalDistance is { } distance)
            Execute($"/bmrai maxdistanceslot {distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        if (originalSlot is >= 0 and < 8)
            Execute($"/bmrai follow slot{originalSlot.Value + 1}");
        diagnostics.Write("BMR跟随", "已关闭 BMR 脱战跟随并恢复原队友槽位。");
    }

    private static bool TryGetBossModPartySlot(ulong contentId, out int slot)
    {
        slot = -1;
        try
        {
            var type = GetActiveBossModManagerType();
            var instance = type?.GetField("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            var worldState = type?.GetProperty("WorldState", BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance);
            var party = worldState?.GetType().GetField("Party", BindingFlags.Instance | BindingFlags.Public)?.GetValue(worldState);
            var members = party?.GetType().GetField("Members", BindingFlags.Instance | BindingFlags.Public)?.GetValue(party) as Array;
            if (members == null) return false;
            for (var index = 1; index < Math.Min(8, members.Length); ++index)
            {
                var member = members.GetValue(index);
                if (member?.GetType().GetField("ContentId", BindingFlags.Instance | BindingFlags.Public)?.GetValue(member) is ulong id
                    && id == contentId)
                {
                    slot = index;
                    return true;
                }
            }
        }
        catch { /* BMR may be updating its party table during zone transitions. */ }
        return false;
    }

    private static Type? GetActiveBossModManagerType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(assembly => assembly.GetName().Name?.StartsWith("BossMod", StringComparison.OrdinalIgnoreCase) == true))
        {
            var type = assembly.GetType("BossMod.AI.AIManager", throwOnError: false);
            if (type?.GetField("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) != null)
                return type;
        }
        return null;
    }

    public void ApplyLazyLootRollMode()
    {
        if (!LazyLootInstalled)
        {
            lazyLootArmed = false;
            appliedLazyLootRollMode = null;
            return;
        }

        if (lazyLootArmed && appliedLazyLootRollMode == configuration.LazyLootRollMode)
        {
            return;
        }

        var command = configuration.LazyLootRollMode switch
        {
            LazyLootRollMode.Need => "need",
            LazyLootRollMode.Greed => "greed",
            LazyLootRollMode.Pass => "pass",
            _ => "need",
        };

        Execute($"/fulf {command}");
        Execute("/fulf on");
        lazyLootArmed = true;
        appliedLazyLootRollMode = configuration.LazyLootRollMode;
    }

    public void SetNavigating(bool navigating)
    {
        if (!configuration.EnableAeAssistIntegration || !AeAssistInstalled)
        {
            if (!configuration.EnableAeAssistIntegration && aeTargetingEnabled == false && AeAssistInstalled)
            {
                Execute("/aeTargetSelector on");
            }

            aeTargetingEnabled = null;
            return;
        }

        var desired = !navigating;
        if (aeTargetingEnabled == desired)
        {
            return;
        }

        Execute(desired ? "/aeTargetSelector on" : "/aeTargetSelector off");
        aeTargetingEnabled = desired;
    }

    private void StartBossMod()
    {
        Execute("/bmrai followcombat on");
        Execute("/bmrai followmodule on");
        Execute("/bmrai followtarget on");
        Execute("/bmrai followoutofcombat off");
        Execute("/bmrai on");
        bossModArmed = true;
    }

    private void StopBossMod()
    {
        Execute("/bmrai followoutofcombat off");
        Execute("/bmrai followmodule off");
        Execute("/bmrai followcombat off");
        Execute("/bmrai followtarget off");
        Execute("/bmrai off");
    }

    private bool TryCaptureBossModFollowSettings()
    {
        if (dungeonFollowOriginalDistance != null && dungeonFollowOriginalSlot != null) return true;
        if (DateTime.UtcNow < nextBossModSettingsReadUtc) return false;

        try
        {
            var managerType = GetActiveBossModManagerType();
            var config = managerType?.GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            var configType = config?.GetType();
            var distance = configType?.GetField("MaxDistanceToSlot", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
            var slot = configType?.GetField("FollowSlot", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
            var outOfCombat = configType?.GetField("FollowOutOfCombat", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
            var target = configType?.GetField("FollowTarget", BindingFlags.Instance | BindingFlags.Public)?.GetValue(config);
            if (distance is not float originalDistance || slot is not int originalSlot || originalSlot is < 0 or > 7
                || outOfCombat is not bool originalOutOfCombat || target is not bool originalTarget)
                throw new InvalidOperationException($"BMR AI 配置不可读：AIManager={managerType != null}，config={config != null}，distance={distance?.GetType().Name ?? "null"}，slot={slot?.GetType().Name ?? "null"}。");

            dungeonFollowOriginalDistance = originalDistance;
            dungeonFollowOriginalSlot = originalSlot;
            dungeonFollowOriginalTarget = originalTarget;
            nextBossModSettingsReadUtc = DateTime.MinValue;
            diagnostics.Write("BMR跟随", $"已保存原队友槽位距离 {originalDistance:F1}y，原跟随槽位 {originalSlot + 1}。");
            return true;
        }
        catch (Exception ex)
        {
            nextBossModSettingsReadUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            diagnostics.WriteException("BMR跟随", "保存 BMR 原设置", ex);
            Plugin.Log.Warning(ex, "Could not preserve BMR follow settings; dungeon follow disabled.");
            return false;
        }
    }

    private bool TrySetBossModFollowSettings(bool outOfCombat, bool target, float distance)
    {
        try
        {
            var config = GetBossModConfig();
            if (config == null) return false;
            var type = config.GetType();
            var oocField = type.GetField("FollowOutOfCombat", BindingFlags.Instance | BindingFlags.Public);
            var targetField = type.GetField("FollowTarget", BindingFlags.Instance | BindingFlags.Public);
            var distanceField = type.GetField("MaxDistanceToSlot", BindingFlags.Instance | BindingFlags.Public);
            if (oocField == null || targetField == null || distanceField == null) return false;
            oocField.SetValue(config, outOfCombat);
            targetField.SetValue(config, target);
            distanceField.SetValue(config, distance);
            return true;
        }
        catch (Exception ex)
        {
            diagnostics.WriteException("BMR跟随", "设置 BMR 脱战跟随", ex);
            return false;
        }
    }

    private static object? GetBossModConfig()
        => GetActiveBossModManagerType()?
            .GetField("_config", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);

    private static bool TryReadBossModFollowState(out bool followOutOfCombat, out bool followTarget)
    {
        followOutOfCombat = false;
        followTarget = false;
        try
        {
            var config = GetBossModConfig();
            if (config == null) return false;
            var type = config.GetType();
            if (type.GetField("FollowOutOfCombat")?.GetValue(config) is not bool ooc
                || type.GetField("FollowTarget")?.GetValue(config) is not bool target)
                return false;
            followOutOfCombat = ooc;
            followTarget = target;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPluginLoaded(string internalName)
        => Plugin.PluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded && plugin.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));

    private static bool Execute(string command)
    {
        try
        {
            var dispatched = Plugin.CommandManager.ProcessCommand(command);
            if (!dispatched) Plugin.Log.Warning("External plugin command {Command} is not registered.", command);
            return dispatched;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to execute external plugin command {Command}.", command);
            return false;
        }
    }
}
