using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GroundStation.Core;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct RobotCommand
{
    public byte RobotId;       // 0x01: Dredger, 0x02: Skimmer, 0x03: Transfer
    public short LeftThrust;   // 1000 to 2000 us (1500 = Neutral)
    public short RightThrust;  // 1000 to 2000 us (1500 = Neutral)
    public short RollerPwm;    // -255 to +255 (Bi-directional intake)
    public byte ArmAction;     // 0x00: Hold, 0x01: Lower, 0x02: Raise
    public byte Flags;         // Bit 0: E-Stop, Bit 1: Aux
    public byte Reserved;      // Padding: bots are 10 bytes so frame = 36 bytes
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BroadcastFrame
{
    public ushort SyncMarker;  // 0xAA55
    public ushort SequenceNum; // Incremented every frame
    public RobotCommand Bot1;  // Dredger
    public RobotCommand Bot2;  // Skimmer
    public RobotCommand Bot3;  // Transfer
    public ushort Crc16;       // CRC-16-CCITT
}

public static class PacketProtocol
{
    public const ushort SyncHeader = 0xAA55;
    public const int PacketSize = 36; // 2 + 2 + 3*10 + 2

    public static byte[] Serialize(ushort seqNum, RobotCommand bot1, RobotCommand bot2, RobotCommand bot3)
    {
        byte[] buffer = new byte[PacketSize];
        using var ms = new MemoryStream(buffer);
        using var writer = new BinaryWriter(ms);

        writer.Write(SyncHeader);
        writer.Write(seqNum);

        WriteBot(writer, bot1);
        WriteBot(writer, bot2);
        WriteBot(writer, bot3);

        // Calculate CRC16 over all bytes preceding the checksum (bytes 0..33)
        ushort crc = ComputeCrc16(buffer, 0, PacketSize - 2);
        writer.Write(crc);

        return buffer;
    }

    private static void WriteBot(BinaryWriter writer, RobotCommand bot)
    {
        writer.Write(bot.RobotId);
        writer.Write(bot.LeftThrust);
        writer.Write(bot.RightThrust);
        writer.Write(bot.RollerPwm);
        writer.Write(bot.ArmAction);
        writer.Write(bot.Flags);
        writer.Write(bot.Reserved);
    }

    /// <summary>
    /// Standard CRC-16-CCITT (Polynomial 0x1021, Initial 0xFFFF)
    /// </summary>
    public static ushort ComputeCrc16(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (ushort)((crc << 1) ^ 0x1021);
                }
                else
                {
                    crc <<= 1;
                }
            }
        }
        return crc;
    }
}
