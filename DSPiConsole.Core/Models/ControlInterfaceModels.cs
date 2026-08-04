using System;

namespace DSPiConsole.Core.Models;

// ─────────────────────────────────────────────────────────────────────────────
// UART / I2C control interfaces (firmware control_interfaces_spec.md; opcodes
// 0xF5–0xF9). The device exposes the same vendor command set over an external
// UART link or as an I2C target, so a host MCU can drive the DSP without USB.
// Both SET commands are USB-only (rejected over the external links). All three
// wire structs are 8 bytes, packed, little-endian.
//
// SETs are deferred: the OUT latches, the firmware applies + persists on its main
// loop, and the authoritative PIN_CONFIG_* outcome is read back via 0xF9. Config
// status codes reuse the shared PIN_CONFIG_* namespace (Usb PinConfigResult):
// 0x00 success, 0x01 invalid pin, 0x02 pin in use, 0x05 invalid param
// (baud / I2C address out of range).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Shared constants / defaults for the control interfaces.</summary>
public static class CtrlIfaceLimits
{
    public const byte ProtoVersion = 1;

    public const byte UartDefaultTxPin = 16;   // pin % 4 == 0 (UARTx TX mux)
    public const byte UartDefaultRxPin = 17;   // pin % 4 == 1 (same instance RX)
    public const uint UartDefaultBaud = 115200;
    public const uint UartBaudMin = 9600;
    public const uint UartBaudMax = 1000000;

    /// <summary>Baud rates offered in the picker (device accepts any in range).</summary>
    public static readonly uint[] BaudChoices =
        { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1000000 };

    public const byte I2cDefaultSdaPin = 18;   // even (I2Cx SDA mux)
    public const byte I2cDefaultSclPin = 19;   // next odd GPIO (same instance SCL)
    public const byte I2cDefaultAddress = 0x42;
    public const byte I2cAddressMin = 0x08;
    public const byte I2cAddressMax = 0x77;
}

/// <summary>The 8-byte UART control-interface config (REQ_SET/GET_UART_CONFIG).
/// Framing is fixed 8N1; only baud and the notify flag are configurable.</summary>
public sealed class UartCtrlConfig
{
    public const int WireSize = 8;

    public bool Enabled;
    public byte TxPin = CtrlIfaceLimits.UartDefaultTxPin;
    public byte RxPin = CtrlIfaceLimits.UartDefaultRxPin;
    public bool NotifyEnable;   // push async notification frames (type 0x40)
    public uint Baud = CtrlIfaceLimits.UartDefaultBaud;

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)(Enabled ? 1 : 0);
        b[1] = TxPin;
        b[2] = RxPin;
        b[3] = (byte)(NotifyEnable ? 1 : 0);
        BitConverter.GetBytes(Baud).CopyTo(b, 4);
        return b;
    }

    public static UartCtrlConfig? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new UartCtrlConfig
        {
            Enabled = d[0] != 0,
            TxPin = d[1],
            RxPin = d[2],
            NotifyEnable = d[3] != 0,
            Baud = BitConverter.ToUInt32(d, 4),
        };
    }

    public UartCtrlConfig Clone() => (UartCtrlConfig)MemberwiseClone();

    public bool ValueEquals(UartCtrlConfig o) =>
        o != null && Enabled == o.Enabled && TxPin == o.TxPin && RxPin == o.RxPin
        && NotifyEnable == o.NotifyEnable && Baud == o.Baud;
}

/// <summary>The 8-byte I2C control-interface config (REQ_SET/GET_I2C_CONFIG). The
/// device acts as an I2C target (slave); the host MCU is bus master. Poll-only —
/// no async notifications. Bytes 4..7 are reserved (zero).</summary>
public sealed class I2cCtrlConfig
{
    public const int WireSize = 8;

    public bool Enabled;
    public byte SdaPin = CtrlIfaceLimits.I2cDefaultSdaPin;   // even
    public byte SclPin = CtrlIfaceLimits.I2cDefaultSclPin;   // next odd GPIO
    public byte Address = CtrlIfaceLimits.I2cDefaultAddress; // 7-bit, 0x08..0x77

    public byte[] ToBytes()
    {
        var b = new byte[WireSize];
        b[0] = (byte)(Enabled ? 1 : 0);
        b[1] = SdaPin;
        b[2] = SclPin;
        b[3] = Address;
        // b[4..7] reserved 0
        return b;
    }

    public static I2cCtrlConfig? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new I2cCtrlConfig
        {
            Enabled = d[0] != 0,
            SdaPin = d[1],
            SclPin = d[2],
            Address = d[3],
        };
    }

    public I2cCtrlConfig Clone() => (I2cCtrlConfig)MemberwiseClone();

    public bool ValueEquals(I2cCtrlConfig o) =>
        o != null && Enabled == o.Enabled && SdaPin == o.SdaPin
        && SclPin == o.SclPin && Address == o.Address;
}

/// <summary>The 8-byte live control-interface status (REQ_GET_CTRL_IFACE_STATUS).
/// <c>*Live</c> is true only when the peripheral is actually running — it is false
/// if a stored-but-enabled config's pins collide with the current wiring at boot
/// (the "Inactive" case: config Enabled but not Live).</summary>
public sealed class CtrlIfaceStatus
{
    public const int WireSize = 8;

    public byte UartLastStatus;  // PIN_CONFIG_* of last UART SET
    public bool UartLive;
    public byte I2cLastStatus;   // PIN_CONFIG_* of last I2C SET
    public bool I2cLive;
    public byte ProtoVersion;    // external wire protocol version (1)

    public static CtrlIfaceStatus? FromBytes(byte[] d)
    {
        if (d == null || d.Length < WireSize) return null;
        return new CtrlIfaceStatus
        {
            UartLastStatus = d[0],
            UartLive = d[1] != 0,
            I2cLastStatus = d[2],
            I2cLive = d[3] != 0,
            ProtoVersion = d[4],
        };
    }

    public bool ValueEquals(CtrlIfaceStatus o) =>
        o != null && UartLastStatus == o.UartLastStatus && UartLive == o.UartLive
        && I2cLastStatus == o.I2cLastStatus && I2cLive == o.I2cLive
        && ProtoVersion == o.ProtoVersion;
}
