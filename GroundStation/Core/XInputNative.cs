using System;
using System.Runtime.InteropServices;

namespace GroundStation.Core;

public static class XInputNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [Flags]
    public enum GamepadButtons : ushort
    {
        DPadUp = 0x0001,
        DPadDown = 0x0002,
        DPadLeft = 0x0004,
        DPadRight = 0x0008,
        Start = 0x0010,
        Back = 0x0020,
        LeftThumb = 0x0040,
        RightThumb = 0x0080,
        LeftShoulder = 0x0100,
        RightShoulder = 0x0200,
        A = 0x1000,
        B = 0x2000,
        X = 0x4000,
        Y = 0x8000
    }

    private const string XInput14 = "xinput1_4.dll";
    private const string XInput910 = "xinput9_1_0.dll";

    [DllImport(XInput14, EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetState14(uint dwUserIndex, out XInputState pState);

    [DllImport(XInput910, EntryPoint = "XInputGetState", CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetState910(uint dwUserIndex, out XInputState pState);

    private static bool _useFallback910 = false;

    public const uint ErrorSuccess = 0x0000;
    public const uint ErrorDeviceNotConnected = 0x0467;

    public static bool GetState(uint userIndex, out XInputState state)
    {
        if (!_useFallback910)
        {
            try
            {
                uint result = XInputGetState14(userIndex, out state);
                return result == ErrorSuccess;
            }
            catch (DllNotFoundException)
            {
                _useFallback910 = true;
            }
        }

        try
        {
            uint result = XInputGetState910(userIndex, out state);
            return result == ErrorSuccess;
        }
        catch
        {
            state = default;
            return false;
        }
    }

    public static double NormalizeAxis(short rawValue)
    {
        // Convert signed 16-bit integer [-32768, 32767] to [-1.0, 1.0]
        if (rawValue < -32767) return -1.0;
        return rawValue / 32767.0;
    }

    public static double NormalizeTrigger(byte rawValue)
    {
        // Convert unsigned byte [0, 255] to [0.0, 1.0]
        return rawValue / 255.0;
    }
}
