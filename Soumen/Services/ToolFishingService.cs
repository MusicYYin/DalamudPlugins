using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace Soumen.Services;

/// <summary>Redirects fishing animation resources observed in the runtime snapshot.</summary>
internal sealed unsafe class ToolFishingService : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void* GetResourceSyncDelegate(nint manager, uint* type, char* category,
        uint* hash, byte* path, void* parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void* GetResourceAsyncDelegate(nint manager, uint* type, char* category,
        uint* hash, byte* path, void* parameters, bool isAsync);

    private static readonly HashSet<string> FishingAnimations = new(StringComparer.OrdinalIgnoreCase)
    {
        "chara/action/fishing/idle.tmb",
        "chara/action/fishing/item.tmb",
        "chara/action/fishing/end.tmb",
        "chara/action/fishing/cast_normal.tmb",
        "chara/action/fishing/cast_side.tmb",
        "chara/action/fishing/cast_fly.tmb",
        "chara/action/fishing/retrieve_idle.tmb",
        "chara/action/fishing/reeling_idle.tmb",
        "chara/action/fishing/reeling_fast.tmb",
        "chara/action/fishing/reeling_slow.tmb",
        "chara/action/fishing/wobble_action.tmb",
        "chara/action/fishing/jerk_and_fall.tmb",
        "chara/action/fishing/cancel.tmb",
        "chara/action/fishing/hooking.tmb",
        "chara/action/fishing/short_landing_nq.tmb",
        "chara/action/fishing/short_landing_hq.tmb",
        "chara/action/fishing/normal_landing_nq.tmb",
        "chara/action/fishing/normal_landing_hq.tmb",
        "chara/action/fishing/long_landing_nq.tmb",
        "chara/action/fishing/long_landing_hq.tmb",
        "chara/action/fishing/landing_failure.tmb",
        "chara/action/event_base/event_base_fishing.tmb",
        "chara/action/event_base/event_base_fishing_stand.tmb",
        "chara/action/fishing_chair/idle.tmb",
        "chara/action/fishing_chair/end.tmb",
        "chara/action/fishing_chair/cast_normal.tmb",
        "chara/action/fishing_chair/cast_side.tmb",
        "chara/action/fishing_chair/cast_fly.tmb",
        "chara/action/fishing_chair/retrieve_idle.tmb",
        "chara/action/fishing_chair/reeling_idle.tmb",
        "chara/action/fishing_chair/reeling_fast.tmb",
        "chara/action/fishing_chair/reeling_slow.tmb",
        "chara/action/fishing_chair/wobble_action.tmb",
        "chara/action/fishing_chair/jerk_and_fall.tmb",
        "chara/action/fishing_chair/cancel.tmb",
        "chara/action/fishing_chair/hooking.tmb",
        "chara/action/fishing_chair/short_landing_nq.tmb",
        "chara/action/fishing_chair/short_landing_hq.tmb",
        "chara/action/fishing_chair/normal_landing_nq.tmb",
        "chara/action/fishing_chair/normal_landing_hq.tmb",
        "chara/action/fishing_chair/long_landing_nq.tmb",
        "chara/action/fishing_chair/long_landing_hq.tmb",
        "chara/action/fishing_chair/landing_failure.tmb",
        "chara/action/fishing_chair/sitdown.tmb",
        "chara/action/fishing_chair/standup.tmb",
        "chara/action/fishing_chair/hooking_big.tmb",
        "chara/action/fishing_chair/long_landing_nq_new.tmb",
        "chara/action/fishing_chair/long_landing_hq_new.tmb",
        "chara/action/fishing_chair/long_landing_sitdown.tmb",
        "chara/action/fishing/catch_and_release.tmb",
        "chara/action/fishing_chair/catch_and_release.tmb",
        "chara/action/event_base/event_base_fishing_stand_start.tmb",
        "chara/action/event_base/event_base_fishing_stand_end.tmb",
        "chara/action/fishing/strong_hooking.tmb",
        "chara/action/fishing/precision_hooking.tmb",
        "chara/action/fishing/makie.tmb",
        "chara/action/fishing/sonar.tmb",
        "chara/action/fishing_chair/strong_hooking.tmb",
        "chara/action/fishing_chair/strong_hooking_big.tmb",
        "chara/action/fishing_chair/precision_hooking.tmb",
        "chara/action/fishing_chair/precision_hooking_big.tmb",
        "chara/action/fishing_chair/makie.tmb",
        "chara/action/fishing_chair/item.tmb",
        "chara/action/fishing/bakucho_landing_nq.tmb",
        "chara/action/fishing/bakucho_landing_hq.tmb",
        "chara/action/fishing_chair/bakucho_landing_nq.tmb",
        "chara/action/fishing_chair/bakucho_landing_hq.tmb",
        "chara/action/fishing/triple_hooking.tmb",
        "chara/action/fishing_chair/triple_hooking.tmb",
        "chara/action/fishing_chair/triple_hooking_big.tmb",
        "chara/action/fishing/bigsize.tmb",
        "chara/action/fishing/gp_recovery.tmb",
        "chara/action/fishing/ignore_condition_swim.tmb",
        "chara/action/event_base/event_base_fishing_stand_nomal.tmb",
        "chara/action/event_base/event_base_fishing_stand_dynamic.tmb",
        "chara/action/event_base/event_base_fishing_stand_start_measures.tmb",
        "chara/action/event_base/event_base_fishing_stand_dy_no.tmb",
        "chara/action/fishing/retrieve_lure.tmb",
        "chara/action/fishing/reeling_lure.tmb",
        "chara/action/fishing/reeling_lure_s.tmb",
        "chara/action/fishing/reeling_lure_f.tmb",
        "chara/action/fishing/jerk_and_fall_lure.tmb",
        "chara/action/fishing/wobble_action_lure.tmb",
        "chara/action/fishing/stock.tmb",
        "chara/action/fishing_chair/retrieve_lure.tmb",
        "chara/action/fishing_chair/reeling_lure.tmb",
        "chara/action/fishing_chair/reeling_lure_s.tmb",
        "chara/action/fishing_chair/reeling_lure_f.tmb",
        "chara/action/fishing_chair/jerk_and_fall_lure.tmb",
        "chara/action/fishing_chair/wobble_action_lure.tmb",
        "chara/action/fishing_chair/stock.tmb",
        "chara/action/fishing/big_knowledge.tmb",
        "chara/action/fishing/int_recover.tmb",
        "chara/action/fishing/hooking_bt.tmb",
        "chara/action/fishing_chair/hooking_bt.tmb",
        "chara/action/fishing_chair/hooking_big_bt.tmb",
    };

    private readonly Configuration configuration;
    private readonly DiagnosticLogger diagnostics;
    private readonly GCHandle nothingHandle;
    private Hook<GetResourceSyncDelegate>? syncHook;
    private Hook<GetResourceAsyncDelegate>? asyncHook;

    private volatile bool interceptActive;
    private volatile bool interceptGathering;
    private bool failed;
    private int intercepted;

    public ToolFishingService(Configuration configuration, DiagnosticLogger diagnostics)
    {
        this.configuration = configuration;
        this.diagnostics = diagnostics;
        nothingHandle = GCHandle.Alloc(Encoding.ASCII.GetBytes("vfx/path/nothing.avfx\0"), GCHandleType.Pinned);
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        interceptActive = false;
        interceptGathering = false;
        asyncHook?.Dispose();
        syncHook?.Dispose();
        if (nothingHandle.IsAllocated) nothingHandle.Free();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _ = framework;
        interceptActive = configuration.CancelFishingAnimation
            && Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId == 18
            && Plugin.Condition[ConditionFlag.Gathering];
        var job = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId;
        interceptGathering = configuration.CancelGatheringAnimation && (job is 16 or 17)
            && Plugin.Condition[ConditionFlag.Gathering];
        if ((!configuration.CancelFishingAnimation && !configuration.CancelGatheringAnimation) || failed) return;

        if (ExternalHookGuard.Blocks("AutoCancelFSHAnimationHook", true, diagnostics))
        {
            interceptActive = false;
            interceptGathering = false;
            if (syncHook?.IsEnabled == true) syncHook.Disable();
            if (asyncHook?.IsEnabled == true) asyncHook.Disable();
            return;
        }

        if (syncHook == null)
        {
            try
            {
                var syncAddress = ToolHookAddresses.Resolve("_getResourceSyncHook", diagnostics);
                var asyncAddress = ToolHookAddresses.Resolve("_getResourceAsyncHook", diagnostics);
                if (syncAddress == 0 || asyncAddress == 0) { failed = true; return; }
                syncHook = Plugin.GameInteropProvider.HookFromAddress<GetResourceSyncDelegate>(syncAddress, GetSync);
                asyncHook = Plugin.GameInteropProvider.HookFromAddress<GetResourceAsyncDelegate>(asyncAddress, GetAsync);
                syncHook.Enable();
                asyncHook.Enable();
                diagnostics.Write("工具 Hook", "钓鱼与采集动画资源入口已接管。");
            }
            catch (Exception exception)
            {
                failed = true;
                interceptActive = false;
                asyncHook?.Dispose();
                syncHook?.Dispose();
                asyncHook = null;
                syncHook = null;
                diagnostics.Write("工具 Hook", $"钓鱼与采集动画安装失败：{exception.GetType().Name}。");
                Plugin.Log.Error(exception, "Fishing resource hooks failed");
            }
        }
        else
        {
            if (syncHook?.IsEnabled == false) syncHook.Enable();
            if (asyncHook?.IsEnabled == false) asyncHook.Enable();
        }

        var count = Interlocked.Exchange(ref intercepted, 0);
        if (count > 0)
            diagnostics.WriteThrottled("soumen-tools-gathering", "采集动画", $"已改写 {count} 个动画资源请求。", TimeSpan.FromSeconds(10));
    }

    private byte* SelectPath(byte* path)
    {
        if ((!interceptActive && !interceptGathering) || path == null) return path;
        var text = Marshal.PtrToStringAnsi((nint)path);
        if (text == null || !(interceptActive && FishingAnimations.Contains(text))
            && !(interceptGathering && text.StartsWith("chara/action/", StringComparison.OrdinalIgnoreCase)
                && text.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase))) return path;
        Interlocked.Increment(ref intercepted);
        return (byte*)nothingHandle.AddrOfPinnedObject();
    }

    private void* GetSync(nint manager, uint* type, char* category, uint* hash, byte* path, void* parameters)
        => syncHook!.Original(manager, type, category, hash, SelectPath(path), parameters);

    private void* GetAsync(nint manager, uint* type, char* category, uint* hash, byte* path, void* parameters, bool isAsync)
        => asyncHook!.Original(manager, type, category, hash, SelectPath(path), parameters, isAsync);
}
