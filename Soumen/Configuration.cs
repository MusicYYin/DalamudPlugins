using Dalamud.Configuration;
using Dalamud.Plugin;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soumen.Models;

namespace Soumen;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    private const string FileName = "config.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    [JsonIgnore]
    private IDalamudPluginInterface? pluginInterface;

    public int Version { get; set; } = 18;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? PreviousSettings { get; set; }

    public AutomationTask ActiveTask { get; set; } = AutomationTask.None;

    public OperatingMode OperatingMode { get; set; } = OperatingMode.Follow;

    public uint LeaderTreasureMapItemId { get; set; } = 46185;

    public bool AutoRestockLeaderMaps { get; set; } = false;

    public uint LeaderMapMaximumUnitPrice { get; set; } = 100000;


    public bool Enabled { get; set; } = false;

    public bool AutoMount { get; set; } = true;

    public bool AutoDismount { get; set; } = true;

    public bool UseFlight { get; set; } = true;

    public bool EnableAeAssistIntegration { get; set; } = true;

    public bool EnableBossModRebornIntegration { get; set; } = true;

    public LazyLootRollMode LazyLootRollMode { get; set; } = LazyLootRollMode.Need;

    public bool AutoTeleport { get; set; } = true;

    public bool AcceptPartyTeleportRequests { get; set; } = false;

    public bool AutoLeaveTreasureDungeon { get; set; } = false;

    public bool AutoCollectTreasureSacks { get; set; } = true;

    public bool AutoDiscardEnabled { get; set; } = false;

    // Kept for one-time migration from 0.4.0 and earlier.
    public HashSet<uint> AutoDiscardItemIds { get; set; } = [];

    public List<DiscardPreset> AutoDiscardPresets { get; set; } = [];

    public string ActiveAutoDiscardPresetId { get; set; } = string.Empty;

    public UiTheme UiTheme { get; set; } = UiTheme.Rainbow;

    public bool FrontlineRadarEnabled { get; set; } = false;

    public float FrontlineRadarRange { get; set; } = 100f;

    public bool FrontlineRadarLines { get; set; } = false;

    public bool FrontlineRadarJobIcons { get; set; } = true;

    public bool FrontlineRadarBattleHighIcons { get; set; } = true;

    public bool NoBackswingMovement { get; set; } = false;

    public bool CancelFishingAnimation { get; set; } = false;

    public bool CancelGatheringAnimation { get; set; } = false;

    public HashSet<string> ToolFavorites { get; set; } = [];

    public bool ToolSpeedEnabled { get; set; } = false;

    public float ToolSpeedMultiplier { get; set; } = 1.2f;

    public bool ToolMaxAcceleration { get; set; } = false;

    public bool ToolNoFallDamage { get; set; } = false;

    public bool ToolForceMovement { get; set; } = false;

    public bool ToolAntiKnockback { get; set; } = false;

    public bool ToolNoDrop { get; set; } = false;

    public bool ToolIgnoreCharm { get; set; } = false;

    public bool ToolStatusBlock { get; set; } = false;

    public bool ToolVerticalMovement { get; set; } = false;

    public float ToolVerticalOffset { get; set; } = 0f;

    public bool ToolMovingCast { get; set; } = false;

    public float ToolMovingCastWindow { get; set; } = 0.3f;

    public float DirectionalTeleportDistance { get; set; } = 5f;

    public bool DiveTeleportCancelRiseAnimation { get; set; } = false;

    public bool ToolActionRangeEnabled { get; set; } = false;

    public float ToolActionRangeBonus { get; set; } = 2f;

    public bool ToolTargetRadiusEnabled { get; set; } = false;

    public bool ToolNoActionMove { get; set; } = false;

    public float ToolTargetRadius { get; set; } = 2f;

    public bool ToolRecastReduction { get; set; } = false;

    public bool ToolRapidMudra { get; set; } = false;

    public float ToolRecastSeconds { get; set; } = 0.3f;

    public bool ToolCastReduction { get; set; } = false;

    public float ToolCastSeconds { get; set; } = 0.3f;

    public bool TeleportWhenStuck { get; set; } = true;

    public bool DiagnosticMode { get; set; } = false;

    public float StuckSeconds { get; set; } = 8f;

    public float ArrivalTolerance { get; set; } = 8f;

    public float TreasureSpotCorrectionRange { get; set; } = 85f;

    public float DungeonFollowDistance { get; set; } = 3.5f;
    public bool DungeonAutoFollow { get; set; } = true;

    public bool HuntEnabled { get; set; } = false;
    public bool HuntAutoTeleport { get; set; } = true;
    public bool HuntAutoOpenMap { get; set; } = true;
    public bool HuntHighlightLeader { get; set; } = true;
    public bool HuntMuteOtherShouts { get; set; } = false;
    public bool HuntChatNotification { get; set; } = true;
    public bool HuntAutoInstance { get; set; } = true;

    public static Configuration Load(IDalamudPluginInterface pluginInterface)
    {
        var directory = pluginInterface.GetPluginConfigDirectory();
        var path = Path.Combine(directory, FileName);
        Directory.CreateDirectory(directory);

        Configuration configuration;
        if (File.Exists(path))
        {
            try
            {
                configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), SerializerOptions)
                    ?? new Configuration();
                configuration.MigratePreviousToolSettings();
            }
            catch (Exception exception)
            {
                Plugin.Log.Error(exception, "Failed to load Soumen config; using defaults.");
                configuration = new Configuration();
            }
        }
        else
        {
            try
            {
                configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            }
            catch (Exception exception)
            {
                Plugin.Log.Error(exception, "Failed to migrate the old Soumen config; using defaults.");
                configuration = new Configuration();
            }
        }

        configuration.pluginInterface = pluginInterface;
        if (configuration.Version < 14)
        {
            configuration.ActiveTask = configuration.HuntEnabled ? AutomationTask.HuntTrain
                : !configuration.Enabled ? AutomationTask.None
                : configuration.OperatingMode == OperatingMode.Leader
                    ? AutomationTask.TreasureLeader : AutomationTask.TreasureFollow;
        }
        if (!Enum.IsDefined(configuration.ActiveTask)) configuration.ActiveTask = AutomationTask.None;
        configuration.Enabled = configuration.ActiveTask != AutomationTask.None;
        configuration.HuntEnabled = configuration.ActiveTask == AutomationTask.HuntTrain;
        if (configuration.HuntEnabled) configuration.OperatingMode = OperatingMode.Follow;
        if (configuration.AcceptPartyTeleportRequests)
        {
            configuration.AutoTeleport = false;
        }
        else
        {
            configuration.AutoTeleport = true;
        }

        configuration.AutoDiscardItemIds ??= [];
        configuration.AutoDiscardPresets ??= [];
        configuration.NormalizeDiscardPresets();
        // Previous theme numbers had a different meaning. Switch existing
        // installations to rainbow once, then keep the user's later choice.
        if (configuration.Version < 18) configuration.UiTheme = UiTheme.Rainbow;
        if (!Enum.IsDefined(typeof(UiTheme), configuration.UiTheme))
        {
            configuration.UiTheme = UiTheme.Rainbow;
        }
        if (!Enum.IsDefined(typeof(LazyLootRollMode), configuration.LazyLootRollMode))
        {
            configuration.LazyLootRollMode = LazyLootRollMode.Need;
        }
        if (!TreasureMapCatalog.Contains(configuration.LeaderTreasureMapItemId))
        {
            configuration.LeaderTreasureMapItemId = TreasureMapCatalog.Default.ItemId;
        }
        configuration.LeaderMapMaximumUnitPrice = Math.Clamp(configuration.LeaderMapMaximumUnitPrice, 1_000u, 9_999_999u);
        configuration.StuckSeconds = Math.Clamp(configuration.StuckSeconds, 1f, 30f);
        configuration.ArrivalTolerance = Math.Clamp(configuration.ArrivalTolerance, 0f, 30f);
        configuration.TreasureSpotCorrectionRange = Math.Clamp(configuration.TreasureSpotCorrectionRange, 0f, 150f);
        configuration.DungeonFollowDistance = Math.Clamp(configuration.DungeonFollowDistance, 0f, 10f);
        configuration.FrontlineRadarRange = Math.Clamp(configuration.FrontlineRadarRange, 20f, 200f);
        configuration.ToolFavorites ??= [];
        configuration.ToolSpeedMultiplier = Math.Clamp(configuration.ToolSpeedMultiplier, 1f, 5f);
        configuration.ToolActionRangeBonus = Math.Clamp(configuration.ToolActionRangeBonus, 0f, 3f);
        configuration.ToolTargetRadius = Math.Clamp(configuration.ToolTargetRadius, 0f, 5f);
        configuration.ToolRecastSeconds = Math.Clamp(configuration.ToolRecastSeconds, 0f, 1f);
        configuration.ToolCastSeconds = Math.Clamp(configuration.ToolCastSeconds, 0f, 1f);
        configuration.ToolVerticalOffset = Math.Clamp(configuration.ToolVerticalOffset, -10f, 10f);
        configuration.ToolMovingCastWindow = Math.Clamp(configuration.ToolMovingCastWindow, 0f, 1f);
        configuration.DirectionalTeleportDistance = float.IsFinite(configuration.DirectionalTeleportDistance)
            ? Math.Clamp(configuration.DirectionalTeleportDistance, 0.1f, 100f) : 5f;
        configuration.Version = 18;
        configuration.Save();
        return configuration;
    }

    private void MigratePreviousToolSettings()
    {
        if (Version >= 17 || PreviousSettings == null) return;

        // Older tool settings used a six-character prefix. Match by option suffix
        // so saved switches survive the rename without keeping the old name in Soumen.
        foreach (var property in typeof(Configuration).GetProperties()
                     .Where(property => property.Name.StartsWith("Tool", StringComparison.Ordinal)))
        {
            if (property.Name == nameof(ToolSpeedMultiplier)) continue;
            var suffix = property.Name[4..];
            var old = PreviousSettings.FirstOrDefault(pair =>
                pair.Key.Length == suffix.Length + 6 && pair.Key.EndsWith(suffix, StringComparison.Ordinal));
            if (old.Key == null) continue;
            try
            {
                if (property.PropertyType == typeof(bool)) property.SetValue(this, old.Value.GetBoolean());
                else if (property.PropertyType == typeof(float)) property.SetValue(this, old.Value.GetSingle());
            }
            catch (Exception exception) { Plugin.Log.Warning(exception, $"Could not migrate tool option {suffix}"); }
        }

        var previousSpeed = PreviousSettings.FirstOrDefault(pair =>
            pair.Key.Length == "SpeedBonus".Length + 6
            && pair.Key.EndsWith("SpeedBonus", StringComparison.Ordinal));
        if (previousSpeed.Key != null)
        {
            try { ToolSpeedMultiplier = 1f + previousSpeed.Value.GetSingle(); }
            catch (Exception exception) { Plugin.Log.Warning(exception, "Could not migrate movement speed"); }
        }
        PreviousSettings.Clear();
    }

    [JsonIgnore]
    public DiscardPreset ActiveDiscardPreset
    {
        get
        {
            NormalizeDiscardPresets();
            return AutoDiscardPresets.First(preset => preset.Id == ActiveAutoDiscardPresetId);
        }
    }

    public HashSet<uint> ResolveActiveDiscardItemIds()
        => ResolveDiscardItemIds(ActiveDiscardPreset.Id);

    public HashSet<uint> ResolveDiscardItemIds(string presetId)
    {
        var result = new HashSet<uint>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void AddPreset(string id)
        {
            if (!visited.Add(id))
            {
                return;
            }

            var preset = AutoDiscardPresets.FirstOrDefault(candidate => candidate.Id == id);
            if (preset == null)
            {
                return;
            }

            result.UnionWith(preset.ItemIds);
            foreach (var includedId in preset.IncludedPresetIds)
            {
                AddPreset(includedId);
            }
        }

        AddPreset(presetId);
        return result;
    }

    public bool CanIncludeDiscardPreset(string parentId, string candidateId)
    {
        if (parentId == candidateId
            || AutoDiscardPresets.All(preset => preset.Id != parentId)
            || AutoDiscardPresets.All(preset => preset.Id != candidateId))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool ReachesParent(string id)
        {
            if (id == parentId)
            {
                return true;
            }

            if (!visited.Add(id))
            {
                return false;
            }

            var preset = AutoDiscardPresets.FirstOrDefault(candidate => candidate.Id == id);
            return preset != null && preset.IncludedPresetIds.Any(ReachesParent);
        }

        return !ReachesParent(candidateId);
    }

    public DiscardPreset CreateDiscardPreset(string? name = null)
    {
        var preset = new DiscardPreset
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"预设 {AutoDiscardPresets.Count + 1}" : name.Trim(),
        };
        AutoDiscardPresets.Add(preset);
        ActiveAutoDiscardPresetId = preset.Id;
        return preset;
    }

    public bool DeleteDiscardPreset(string presetId)
    {
        if (AutoDiscardPresets.Count <= 1)
        {
            return false;
        }

        var removed = AutoDiscardPresets.RemoveAll(preset => preset.Id == presetId) > 0;
        if (!removed)
        {
            return false;
        }

        foreach (var preset in AutoDiscardPresets)
        {
            preset.IncludedPresetIds.Remove(presetId);
        }

        if (ActiveAutoDiscardPresetId == presetId)
        {
            ActiveAutoDiscardPresetId = AutoDiscardPresets[0].Id;
        }

        return true;
    }

    private void NormalizeDiscardPresets()
    {
        AutoDiscardPresets ??= [];
        AutoDiscardItemIds ??= [];

        if (AutoDiscardPresets.Count == 0)
        {
            AutoDiscardPresets.Add(new DiscardPreset
            {
                Name = "默认",
                ItemIds = [.. AutoDiscardItemIds],
            });
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in AutoDiscardPresets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id) || !usedIds.Add(preset.Id))
            {
                preset.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(preset.Id);
            }

            preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "未命名预设" : preset.Name.Trim();
            preset.ItemIds ??= [];
            preset.IncludedPresetIds ??= [];
        }

        foreach (var preset in AutoDiscardPresets)
        {
            preset.IncludedPresetIds.RemoveWhere(id => id == preset.Id || !usedIds.Contains(id));
        }

        if (!usedIds.Contains(ActiveAutoDiscardPresetId))
        {
            ActiveAutoDiscardPresetId = AutoDiscardPresets[0].Id;
        }

        AutoDiscardItemIds.Clear();
    }

    public void Save()
    {
        if (pluginInterface == null)
        {
            return;
        }

        try
        {
            var directory = pluginInterface.GetPluginConfigDirectory();
            var path = Path.Combine(directory, FileName);
            var temporaryPath = path + ".tmp";
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temporaryPath, path, true);
        }
        catch (Exception exception)
        {
            Plugin.Log.Error(exception, "Failed to save Soumen config.");
        }
    }
}
