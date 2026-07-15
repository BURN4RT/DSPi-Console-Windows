using System;

namespace DSPiConsole.Core.Models;

/// <summary>I2S clock modes and slave-lock status (firmware clock_pins_spec.md).</summary>
public static class I2sClock
{
    public const byte ModeMaster = 0;
    public const byte ModeSlave = 1;

    public const byte PinModeUnified = 0;
    public const byte PinModeSplit = 1;

    public const byte BckRoleMaster = 0;   // master/unified clock pair
    public const byte BckRoleSlave = 1;    // slave clock pair (SPLIT mode only)
}

/// <summary>I2S slave-clock lock state (I2sSlaveStatusPacket.state).</summary>
public enum I2sSlaveState : byte
{
    Inactive = 0, Acquiring = 1, Relocking = 2, Locked = 3
}

/// <summary>
/// The 16-byte I2sSlaveStatusPacket (REQ_GET_I2S_SLAVE_STATUS, 0x8A). Live state of
/// the I2S input in slave-clock mode. Little-endian.
/// </summary>
public sealed class I2sSlaveStatus
{
    public const int WireSize = 16;

    public I2sSlaveState State;   // [0]
    public byte ClockMode;        // [1] 0=master, 1=slave
    public byte LockCount;        // [2]
    public byte LossCount;        // [3]
    public uint DetectedRate;     // [4] Hz
    public uint MeasuredHz;       // [8] raw measured Hz (0 in master)

    public bool IsSlave => ClockMode == I2sClock.ModeSlave;
    public bool IsLocked => State == I2sSlaveState.Locked;

    public string StateText => State switch
    {
        I2sSlaveState.Inactive => "Inactive",
        I2sSlaveState.Acquiring => "Acquiring",
        I2sSlaveState.Relocking => "Relocking",
        I2sSlaveState.Locked => "Locked",
        _ => "Unknown"
    };

    public string DetectedRateText => DetectedRate > 0 ? $"{DetectedRate / 1000.0:0.0} kHz" : "—";

    public static I2sSlaveStatus? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new I2sSlaveStatus
        {
            State = (I2sSlaveState)d[0],
            ClockMode = d[1],
            LockCount = d[2],
            LossCount = d[3],
            DetectedRate = BitConverter.ToUInt32(d, 4),
            MeasuredHz = BitConverter.ToUInt32(d, 8),
        };
    }
}
