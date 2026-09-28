using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace Soumen.Services;

internal enum TeleportDirection
{
    Forward,
    Backward,
    Left,
    Right,
    Up,
    Down,
}

/// <summary>One-shot, same-territory teleport using the client's GameObject position function.</summary>
internal sealed unsafe class CoordinateTeleportService(DiagnosticLogger diagnostics)
{
    private const string FlightPositionSignature = "4C ?? ?? ?? ?? ?? ?? 48 ?? ?? 48 ?? ?? BF";

    public bool TryTeleportInDirection(TeleportDirection direction, float distance, out string status)
    {
        if (!float.IsFinite(distance) || distance < 0.1f || distance > 100f)
        {
            status = "方向传送距离需在 0.1～100 y 之间。";
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || !float.IsFinite(player.Rotation))
        {
            status = "角色未就绪，无法读取当前朝向。";
            return false;
        }

        // FFXIV's positive rotation faces +X, but its horizontal right axis is -X at zero rotation.
        var forward = new Vector3(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var offset = direction switch
        {
            TeleportDirection.Forward => forward,
            TeleportDirection.Backward => -forward,
            TeleportDirection.Left => -right,
            TeleportDirection.Right => right,
            TeleportDirection.Up => Vector3.UnitY,
            TeleportDirection.Down => -Vector3.UnitY,
            _ => Vector3.Zero,
        };
        if (offset == Vector3.Zero)
        {
            status = "不支持该传送方向。";
            return false;
        }

        return TryTeleport(player.Position + offset * distance, out status);
    }

    public bool TryDiveTeleport(Vector3 destination, out string status)
    {
        if (!IsValidDestination(destination))
        {
            status = "请输入有效的 X、Y、Z 世界坐标。";
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.Address == 0 || Plugin.ClientState.TerritoryType == 0
            || !Plugin.Condition[ConditionFlag.Diving]
            || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            status = "仅在角色潜水且地图稳定时可使用潜水传送。";
            return false;
        }

        try
        {
            var signature = Plugin.SigScanner.ScanText(FlightPositionSignature);
            if (signature == 0)
            {
                status = "当前游戏版本未找到潜水位置结构。";
                diagnostics.Write("工具传送", status);
                return false;
            }

            // The RIP-relative instruction addresses the flight/dive state. Validate its live XYZ
            // against the player position before writing; obsolete offsets must fail closed.
            var ripRelativeOffset = Marshal.ReadInt32(signature + 3);
            var state = signature + 7 + ripRelativeOffset + 0x5520 + 0x150;
            var position = state + 16;
            using var process = Process.GetCurrentProcess();
            var module = process.MainModule;
            if (module == null || position < module.BaseAddress
                || position > module.BaseAddress + module.ModuleMemorySize - 12)
            {
                status = "潜水位置结构不在游戏模块内，已停止写入。";
                diagnostics.Write("工具传送", status);
                return false;
            }
            var current = new Vector3(
                BitConverter.Int32BitsToSingle(Marshal.ReadInt32(position)),
                BitConverter.Int32BitsToSingle(Marshal.ReadInt32(position + 4)),
                BitConverter.Int32BitsToSingle(Marshal.ReadInt32(position + 8)));
            if (!float.IsFinite(current.X) || !float.IsFinite(current.Y) || !float.IsFinite(current.Z)
                || Vector3.DistanceSquared(current, player.Position) > 9f)
            {
                status = "潜水位置结构与角色坐标不符，已停止写入。";
                diagnostics.Write("工具传送", status);
                return false;
            }

            Marshal.WriteInt32(position, BitConverter.SingleToInt32Bits(destination.X));
            Marshal.WriteInt32(position + 4, BitConverter.SingleToInt32Bits(destination.Y));
            Marshal.WriteInt32(position + 8, BitConverter.SingleToInt32Bits(destination.Z));
            status = $"已写入潜水坐标：X={destination.X:F2}，Y={destination.Y:F2}，Z={destination.Z:F2}。";
            diagnostics.Write("工具传送", $"当前地图 {Plugin.ClientState.TerritoryType}：{status}");
            return true;
        }
        catch (Exception exception)
        {
            status = $"潜水传送失败：{exception.GetType().Name}。";
            diagnostics.WriteException("工具传送", "潜水位置写入", exception);
            return false;
        }
    }

    public bool TryTeleport(Vector3 destination, out string status)
    {
        if (!IsValidDestination(destination))
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

    private static bool IsValidDestination(Vector3 destination)
        => float.IsFinite(destination.X) && float.IsFinite(destination.Y) && float.IsFinite(destination.Z)
            && Math.Abs(destination.X) <= 100_000f && Math.Abs(destination.Y) <= 100_000f
            && Math.Abs(destination.Z) <= 100_000f;
}
