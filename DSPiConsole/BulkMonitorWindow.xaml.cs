using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DSPiConsole.Usb;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace DSPiConsole;

/// <summary>
/// Live view of every packet read on the bulk notification endpoint (EP3).
/// Shows IDLE keep-alives, decoded v2 PARAM_CHANGED / BULK_INVALIDATED /
/// PRESET_LOADED events, unknown event IDs, and malformed packets. Useful for
/// debugging firmware notifications, verifying GPIO-driven param changes,
/// and confirming origin tags on multi-host setups.
/// </summary>
public sealed partial class BulkMonitorWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // Bounded ring buffer of decoded lines. 1000 keeps memory predictable and
    // TextBlock update cost reasonable (~80 KB max text).
    private const int MaxLines = 1000;
    private readonly Queue<string> _lines = new(MaxLines + 1);

    private readonly DspDevice _device;
    private bool _paused;
    private long _totalPackets;
    private long _droppedIdleCount;

    public BulkMonitorWindow(DspDevice device)
    {
        InitializeComponent();
        _device = device;

        // Title bar styling — matches StatsWindow / other secondary windows.
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        appWindow?.Resize(new Windows.Graphics.SizeInt32((int)(900 * dpiScale), (int)(600 * dpiScale)));
        if (appWindow != null)
        {
            appWindow.Title = "Bulk Endpoint Monitor";
            if (appWindow.TitleBar is { } titleBar)
            {
                titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
                titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
                titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
                titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 140);
                titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
            }
        }

        PauseButton.Click += OnPauseClick;
        ClearButton.Click += OnClearClick;
        UpdateStatusBar();

        // Subscribe to raw packets. Fires on the notify thread; we marshal to UI.
        _device.NotifyPacketReceived += OnPacketReceived;
        Closed += (_, _) =>
        {
            _device.NotifyPacketReceived -= OnPacketReceived;
        };
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Resume" : "Pause";
        UpdateStatusBar();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _lines.Clear();
        _totalPackets = 0;
        _droppedIdleCount = 0;
        LogText.Text = "";
        UpdateStatusBar();
    }

    private void OnPacketReceived(object? sender, NotifyPacket pkt)
    {
        // Notification thread → marshal everything UI-touching onto the
        // dispatcher. The decoder runs there too — it's cheap (~tens of µs
        // for ≤16-byte payloads) and reading the CheckBox toggles off-thread
        // is unsafe in WinUI 3.
        bool isIdle = pkt.Data.Length < 4;
        DispatcherQueue.TryEnqueue(() =>
        {
            _totalPackets++;
            if (isIdle && ShowIdleCheck.IsChecked != true)
            {
                _droppedIdleCount++;
                UpdateStatusBar();
                return;
            }
            if (_paused)
            {
                UpdateStatusBar();
                return;
            }
            var line = NotifyPacketDecoder.Format(pkt, ShowHexCheck.IsChecked == true);
            AppendLine(line);
            UpdateStatusBar();
            if (AutoScrollCheck.IsChecked == true)
                LogScroll.ChangeView(null, double.MaxValue, null, disableAnimation: true);
        });
    }

    private void AppendLine(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > MaxLines) _lines.Dequeue();

        // Rebuild text from the bounded queue. For ~1000 lines this is a few
        // hundred microseconds and avoids string concat in a long-lived buffer.
        var sb = new StringBuilder(_lines.Count * 80);
        foreach (var l in _lines)
        {
            sb.Append(l);
            sb.Append('\n');
        }
        // Drop trailing newline to keep the last line tight against the bottom.
        if (sb.Length > 0) sb.Length--;
        LogText.Text = sb.ToString();
    }

    private void UpdateStatusBar()
    {
        StatusText.Text = _paused ? "Paused" : "Running";
        CountText.Text = string.Format(CultureInfo.InvariantCulture,
            "{0} packets   ({1} IDLE hidden)   {2} lines shown",
            _totalPackets, _droppedIdleCount, _lines.Count);
    }
}

/// <summary>
/// Decodes a <see cref="NotifyPacket"/> into one human-readable log line.
/// Shares offset constants with <c>BulkParamsParser</c> and event IDs with
/// <c>DspDevice.ProcessNotifyPacket</c>, but emits a richer description
/// (origin tag names, offset → field decoding, optional hex dump).
/// </summary>
internal static class NotifyPacketDecoder
{
    // Mirrors the offsets used by DspDevice.ProcessNotifyPacket and
    // BulkParamsParser — single source of truth on the firmware side.
    private const int ChannelNamesWireOffset = 2480;
    private const int WireChannelNameLen = 32;
    private const int MasterVolumeWireOffset = 2880;
    private const int InputSourceWireOffset = 2896;
    private const int UserVolumeWireOffset = 2928;

    public static string Format(NotifyPacket pkt, bool includeHex)
    {
        var ts = pkt.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var data = pkt.Data;
        var sb = new StringBuilder(128);
        sb.Append('[').Append(ts).Append("] ");

        if (data.Length < 4)
        {
            // 1-byte IDLE keep-alive (or anything else too small for v2 header).
            sb.Append("IDLE  ").Append(data.Length).Append('B');
            if (includeHex) AppendHex(sb, data, data.Length);
            return sb.ToString();
        }

        byte version = data[0];
        byte eventId = data[1];
        // data[2] = flags (must be 0 in v2)
        byte seq = data[3];

        if (version == 0x01)
        {
            // Legacy v1 master-volume packet — DspDevice currently ignores it.
            sb.Append("v1  ").Append(data.Length).Append('B');
            if (includeHex) AppendHex(sb, data, data.Length);
            return sb.ToString();
        }
        if (version != 0x02)
        {
            sb.Append("UNKNOWN version=0x").Append(version.ToString("X2"));
            if (includeHex) AppendHex(sb, data, data.Length);
            return sb.ToString();
        }

        switch (eventId)
        {
            case 0x02: FormatParamChanged(sb, data, seq); break;
            case 0x03: FormatBulkInvalidated(sb, data, seq); break;
            case 0x04: FormatPresetLoaded(sb, data, seq); break;
            default:
                sb.Append("UNKNOWN event=0x").Append(eventId.ToString("X2"))
                  .Append(" seq=").Append(seq);
                break;
        }
        if (includeHex) AppendHex(sb, data, data.Length);
        return sb.ToString();
    }

    private static void FormatParamChanged(StringBuilder sb, byte[] data, byte seq)
    {
        if (data.Length < 12)
        {
            sb.Append("PARAM_CHANGED (truncated, ").Append(data.Length).Append("B)");
            return;
        }
        ushort offset = BitConverter.ToUInt16(data, 4);
        ushort size = BitConverter.ToUInt16(data, 6);
        byte source = data[8];
        sb.Append("PARAM_CHANGED  seq=").Append(seq)
          .Append(" src=").Append(SourceName(source))
          .Append(" offset=").Append(offset)
          .Append(" size=").Append(size);
        // Region decoding — same offset map ProcessNotifyPacket dispatches on.
        var region = DescribeOffset(offset, size);
        if (region != null) sb.Append(" (").Append(region).Append(')');

        // Payload summary for the common cases.
        if (12 + size <= data.Length)
            AppendPayloadSummary(sb, data, 12, size, offset);
    }

    private static void FormatBulkInvalidated(StringBuilder sb, byte[] data, byte seq)
    {
        sb.Append("BULK_INVALIDATED  seq=").Append(seq);
        if (data.Length >= 5)
            sb.Append(" src=").Append(SourceName(data[4]));
    }

    private static void FormatPresetLoaded(StringBuilder sb, byte[] data, byte seq)
    {
        sb.Append("PRESET_LOADED  seq=").Append(seq);
        if (data.Length >= 5)
            sb.Append(" slot=").Append(data[4]);
    }

    private static string? DescribeOffset(int offset, int size)
    {
        // EQ band region: 11 channels × 12 bands × 16 bytes = 2112 bytes
        const int OffsetEq = 368;
        const int WireBandSize = 16;
        const int WireMaxBands = 12;
        const int WireMaxChannels = 11;
        if (size == WireBandSize
            && offset >= OffsetEq
            && offset < OffsetEq + WireMaxChannels * WireMaxBands * WireBandSize
            && (offset - OffsetEq) % WireBandSize == 0)
        {
            int flat = (offset - OffsetEq) / WireBandSize;
            return $"eq[ch={flat / WireMaxBands},band={flat % WireMaxBands}]";
        }
        // Channel name
        if (size == WireChannelNameLen
            && offset >= ChannelNamesWireOffset
            && offset < ChannelNamesWireOffset + 11 * WireChannelNameLen
            && (offset - ChannelNamesWireOffset) % WireChannelNameLen == 0)
        {
            return $"channel_names[{(offset - ChannelNamesWireOffset) / WireChannelNameLen}]";
        }
        if (size == 1 && offset == InputSourceWireOffset) return "input_source";
        if (size == 4 && offset == MasterVolumeWireOffset) return "master_volume_db";
        if (size == 4 && offset == UserVolumeWireOffset) return "user_volume_db";
        if (size == 1 && offset == UserVolumeWireOffset + 4) return "user_mute";
        return null;
    }

    private static void AppendPayloadSummary(StringBuilder sb, byte[] data, int payloadOff, int size, int wireOffset)
    {
        // Recognized scalar payloads — keep these tight; the hex dump option
        // covers everything else.
        if (size == 1 && wireOffset == InputSourceWireOffset)
        {
            sb.Append(" → ").Append(data[payloadOff] == 1 ? "SPDIF" : "USB");
            return;
        }
        if (size == 4 && (wireOffset == MasterVolumeWireOffset || wireOffset == UserVolumeWireOffset))
        {
            float db = BitConverter.ToSingle(data, payloadOff);
            sb.Append(" → ").Append(db.ToString("F1", CultureInfo.InvariantCulture)).Append(" dB");
            return;
        }
        if (size == 16)
        {
            // WireBandParams: type(1), bypass(1), reserved(2), freq(4), Q(4), gain(4)
            // Heuristic: if first byte looks like a FilterType (0..7) and the rest
            // are plausible floats, decode it. Otherwise stay quiet.
            byte type = data[payloadOff];
            if (type <= 7)
            {
                byte bypass = data[payloadOff + 1];
                float freq = BitConverter.ToSingle(data, payloadOff + 4);
                float q = BitConverter.ToSingle(data, payloadOff + 8);
                float gain = BitConverter.ToSingle(data, payloadOff + 12);
                sb.Append(" → type=").Append(type)
                  .Append(bypass == 1 ? " BYPASSED" : "")
                  .Append(" f=").Append(freq.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(" q=").Append(q.ToString("F2", CultureInfo.InvariantCulture))
                  .Append(" g=").Append(gain.ToString("F1", CultureInfo.InvariantCulture));
            }
        }
    }

    private static string SourceName(byte src) => src switch
    {
        0 => "Unknown",
        1 => "HostSet",
        2 => "BulkSet",
        3 => "Preset",
        4 => "Factory",
        5 => "Gpio",
        6 => "Internal",
        _ => $"src({src})"
    };

    private static void AppendHex(StringBuilder sb, byte[] data, int len)
    {
        sb.Append("  | ");
        int show = Math.Min(len, 32);
        for (int i = 0; i < show; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        if (len > show) sb.Append(" …");
    }
}
