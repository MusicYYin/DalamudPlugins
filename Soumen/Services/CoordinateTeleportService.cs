using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Soumen.Services;

/// <summary>One-shot, same-territory teleport using the client's GameObject position function.</summary>
internal sealed unsafe class CoordinateTeleportService(DiagnosticLogger diagnostics)
{
    public bool TryTeleport(Vector3 destination, out string status)
    {
        if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || !float.IsFinite(destination.Z)
            || Math.Abs(destination.X) > 100_000f || Math.Abs(destination.Y) > 100_000f
            || Math.Abs(destination.Z) > 100_000f)
        {
            status = "请输入有效的 X、Y、Z 世界坐标。";
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.Address == 0 || Plugin.ClientState.TerritoryType == 0
            || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.WatchingCutscene]
            || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            status = "当前无法传送：角色未就绪或正在切换区域、观看过场。";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InFlight] || Plugin.Condition[ConditionFlag.Diving])
        {
            status = "当前版本的坐标传送仅支持地面状态，飞行和潜水需要单独实现。";
            return false;
        }

        try
        {
            var gameObject = (GameObject*)player.Address;
            gameObject->SetPosition(destination.X, destination.Y, destination.Z);
            if (Vector3.DistanceSquared(gameObject->Position, destination) > 0.25f)
            {
                status = "客户端未确认位置变更；此地图或当前状态可能不支持坐标传送。";
                diagnostics.Write("工具传送", status);
                return false;
            }
            status = $"已写入世界坐标：X={destination.X:F2}，Y={destination.Y:F2}，Z={destination.Z:F2}。";
            diagnostics.Write("工具传送", $"当前地图 {Plugin.ClientState.TerritoryType}：{status}");
            return true;
        }
        catch (Exception ex)
        {
            status = $"坐标传送失败：{ex.GetType().Name}。";
            diagnostics.WriteException("工具传送", "调用坐标传送", ex);
            Plugin.Log.Error(ex, "Coordinate teleport failed");
            return false;
        }
    }
}
