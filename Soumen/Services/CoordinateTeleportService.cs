using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

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
    // The optional rise-animation shortcut sends a movement update before DiveEnd.
    // Changing GameObject.Position alone only changes the local copy of the position.
    private const string NormalPositionOpcode = "41 B8 ?? ?? ?? ?? F6 C2";
    private const string SendPositionPacketCall = "E8 ?? ?? ?? ?? 48 8B D6 48 8B CF E8 ?? ?? ?? ?? 48 8B 8C 24";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SendPositionPacketDelegate(nint networkModuleProxy, byte* packet, uint a3, uint a4);

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct PositionUpdatePacket
    {
        [FieldOffset(0)] public uint Opcode;
        [FieldOffset(8)] public uint Length;
        [FieldOffset(32)] public float Rotation;
        [FieldOffset(36)] public uint Move;
        [FieldOffset(40)] public Vector3 Position;
    }

    private static SendPositionPacketDelegate? sendPositionPacket;
    private static uint positionOpcode;

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

    public bool TryDiveTeleportToFlag(bool cancelRiseAnimation, out Vector3 destination, out string status)
    {
        destination = default;
        var player = Plugin.ObjectTable.LocalPlayer;
        var map = AgentMap.Instance();
        if (player == null || map == null || map->FlagMarkerCount == 0)
        {
            status = "请先在当前地图设置旗标。";
            return false;
        }

        var flag = map->FlagMapMarkers[0];
        if (flag.TerritoryId != Plugin.ClientState.TerritoryType || flag.MapId == 0)
        {
            status = "旗标不在当前地图，无法传送。";
            return false;
        }

        destination = new Vector3(flag.XFloat, player.Position.Y, flag.YFloat);
        return TryDiveTeleport(destination, cancelRiseAnimation, out status);
    }

    public bool TryDiveTeleport(Vector3 destination, bool cancelRiseAnimation, out string status)
    {
        if (!IsValidDestination(destination))
        {
            status = "请输入有效的 X、Y、Z 世界坐标。";
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.Address == 0 || !float.IsFinite(player.Rotation)
            || Plugin.ClientState.TerritoryType == 0
            || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            status = "角色未就绪或正在切换区域、观看过场，无法潜水传送。";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Diving])
        {
            status = "潜水传送请在陆地脱战后使用。";
            return false;
        }

        var originalPosition = player.Position;
        try
        {
            if (cancelRiseAnimation)
            {
                if (!TrySendLoweredPosition(player.Rotation, originalPosition, out var error))
                {
                    status = $"无法取消浮起动画：{error}";
                    diagnostics.Write("工具传送", status);
                    return false;
                }
                diagnostics.Write("工具传送", $"浮起动画位置更新已发送：原始 Y={originalPosition.Y:F2}，上报 Y={originalPosition.Y - 100f:F2}。");
            }

            // Neko's TPDive calls the game's location command 607 (DiveEnd) with the
            // destination, packet-encoded player rotation and the mounted correction flag.
            // It is sent from the ground as well; it is not a write to the current dive position.
            const float packetRotationScale = 10430.2195f;
            var rotation = (int)Math.Clamp((player.Rotation + MathF.PI) * packetRotationScale, 0f, 65535f);
            var mountedCorrection = Plugin.Condition[ConditionFlag.Mounted] ? 1 : 0;
            if (!GameMain.ExecuteLocationCommand(607, &destination, rotation, mountedCorrection))
            {
                status = "客户端未接受潜水传送请求。";
                diagnostics.Write("工具传送", status);
                return false;
            }

            status = string.Empty;
            diagnostics.Write("工具传送", $"潜水传送已发送：地图={Plugin.ClientState.TerritoryType}，"
                + $"X={destination.X:F2}，Y={destination.Y:F2}，Z={destination.Z:F2}；取消浮起动画={cancelRiseAnimation}。");
            return true;
        }
        catch (Exception exception)
        {
            status = $"潜水传送失败：{exception.GetType().Name}。";
            diagnostics.WriteException("工具传送", "潜水传送命令", exception);
            return false;
        }
    }

    private static bool TrySendLoweredPosition(float rotation, Vector3 originalPosition, out string error)
    {
        using var process = Process.GetCurrentProcess();
        var gameModule = process.MainModule;
        if (gameModule == null || gameModule.ModuleMemorySize != 0x380A000)
        {
            error = "游戏客户端版本与已验证的移动包入口不一致";
            return false;
        }

        if (sendPositionPacket == null)
        {
            var opcodeAddress = Plugin.SigScanner.ScanText(NormalPositionOpcode);
            if (!SafeMemory.Read<uint>(opcodeAddress + 2, out var opcode) || opcode is 0 or > ushort.MaxValue)
            {
                error = "无法读取当前位置更新包编号";
                return false;
            }

            var callAddress = Plugin.SigScanner.ScanText(SendPositionPacketCall);
            if (!SafeMemory.Read<int>(callAddress + 1, out var displacement))
            {
                error = "无法读取移动包发送入口";
                return false;
            }

            var target = callAddress + 5 + displacement;
            if (target < gameModule.BaseAddress
                || target - gameModule.BaseAddress >= gameModule.ModuleMemorySize)
            {
                error = "移动包发送入口不属于当前游戏客户端";
                return false;
            }

            sendPositionPacket = Marshal.GetDelegateForFunctionPointer<SendPositionPacketDelegate>(target);
            positionOpcode = opcode;
        }

        var framework = Framework.Instance();
        var networkModuleProxy = framework == null ? null : framework->NetworkModuleProxy;
        if (networkModuleProxy == null)
        {
            error = "游戏网络模块未就绪";
            return false;
        }

        var lowered = new Vector3(originalPosition.X, originalPosition.Y - 100f, originalPosition.Z);
        // The movement update has four variants. Send all of them as the original
        // packet helper does, before submitting the DiveEnd location command.
        for (uint variant = 0; variant < 4; variant++)
        {
            var packet = new PositionUpdatePacket
            {
                Opcode = positionOpcode,
                Length = 40,
                Rotation = rotation,
                Move = variant << 16,
                Position = lowered,
            };
            sendPositionPacket((nint)networkModuleProxy, (byte*)&packet, 0, 0x9876543);
        }

        error = string.Empty;
        return true;
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
