using System;
using System.Threading.Tasks;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Applies device-pushed PARAM_CHANGED notifications for parameters that aren't
/// covered by a dedicated typed handler, plus the discrete hardware-state events.
/// This keeps the UI live when a parameter is changed by something other than this
/// app — a control surface (source=GPIO), the OS, another host, or a UART/I2C
/// controller. HostSet echoes (our own writes) are ignored.
///
/// <para>Scalar fields are applied in place (backing field + PropertyChanged, no
/// device write). Feature blocks reuse the existing Fetch* readers (which already
/// update the VM without echoing), run off the notify thread via Task.Run.</para>
/// </summary>
public partial class MainViewModel
{
    /// <summary>Wire the generic PARAM_CHANGED + discrete-event notifications.
    /// Called once from the constructor.</summary>
    private void WireParamNotifications()
    {
        _device.ParamChangedNotified += (_, n) =>
        {
            // Our own EP0 writes echo back as HostSet — ignore to avoid loops.
            if (n.Source == ParamSource.HostSet) return;
            OnGenericParamChanged(n);
        };

        _device.InputFormatNotified += (_, _channels) =>
        {
            // The device negotiated a new USB input channel count — re-read the
            // authoritative value from Windows immediately (instead of waiting for
            // the periodic poll).
            Task.Run(RefreshUsbInputChannelCount);
        };

        _device.StatusEventNotified += (_, eventId) =>
        {
            switch (eventId)
            {
                case 0x08: Task.Run(() => { var s = _device.GetAdatStatus(); if (s != null) _dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(AdatEnabled))); FetchAdatConfig(); }); break;
                case 0x09: Task.Run(RefreshI2sSlaveStatus); break;
                case 0x0B: Task.Run(RefreshAdatInputStatus); break;
                // 0x07 siggen: the Test Signals window polls its own status.
            }
        };
    }

    private void OnGenericParamChanged(ParamChangedNotification n)
    {
        int off = n.Offset;

        // ── Feature blocks: reuse the tested Fetch* readers (off the notify thread).
        // Loudness (global +5..+15, but not +4 bypass).
        if (off >= BulkParamsParser.OffsetGlobal + 5 && off < BulkParamsParser.OffsetCrossfeed)
        { Task.Run(FetchLoudness); return; }
        if (off >= BulkParamsParser.OffsetCrossfeed && off < BulkParamsParser.OffsetLegacy)
        { Task.Run(FetchCrossfeed); return; }
        if (off >= BulkParamsParser.OffsetPsybass)
        { Task.Run(FetchPsybass); return; }
        if (off >= BulkParamsParser.OffsetDacHwMute && off < BulkParamsParser.OffsetCrossover)
        { Task.Run(FetchDacHwMute); return; }
        if (off >= BulkParamsParser.OffsetLgSoundSync && off < BulkParamsParser.OffsetUserVolume)
        { Task.Run(FetchLgSoundSync); return; }
        if (off >= BulkParamsParser.OffsetAdat && off < BulkParamsParser.OffsetPsybass)
        { Task.Run(FetchAdatConfig); return; }

        // I2S output-config block (BCK/MCK/clock-pin/slave-BCK).
        if (off >= BulkParamsParser.OffsetI2S && off < BulkParamsParser.OffsetLeveller)
        {
            Task.Run(() =>
            {
                FetchI2SBckPin(); FetchMckEnable(); FetchMckPin(); FetchMckMultiplier();
                FetchI2sClockConfig();
            });
            return;
        }

        // Input-config block: input source is handled by the typed InputSourceNotified;
        // i2s clock mode + ADAT input map to the clock/adat fetchers.
        if (off >= BulkParamsParser.OffsetInputCfg && off < BulkParamsParser.OffsetLgSoundSync)
        {
            int sub = off - BulkParamsParser.OffsetInputCfg;
            if (sub == 11) { Task.Run(FetchI2sClockConfig); return; }
            if (sub is 12 or 13 or 14) { Task.Run(FetchAdatInputConfig); return; }
            // Other input-config fields (pins, rate, channels) — left to the periodic
            // refetch for now.
            return;
        }

        // ── Scalars applied in place (no device write) on the UI thread. ──
        _dispatcher.TryEnqueue(() => ApplyScalarParam(n));
    }

    private void ApplyScalarParam(ParamChangedNotification n)
    {
        int off = n.Offset;
        var p = n.Payload;
        bool changed = true;

        // Master volume.
        if (off == BulkParamsParser.OffsetMasterVol && p.Length >= 4)
        {
            _masterVolumeDb = BitConverter.ToSingle(p, 0);
            OnPropertyChanged(nameof(MasterVolumeDb));
        }
        // Global bypass (offset global+4).
        else if (off == BulkParamsParser.OffsetGlobal + 4 && p.Length >= 1)
        {
            _bypass = p[0] != 0;
            OnPropertyChanged(nameof(Bypass));
            BypassChanged?.Invoke(this, EventArgs.Empty);
        }
        // Per-input preamp (first two inputs shown as L/R).
        else if (off >= BulkParamsParser.OffsetPreamp && off < BulkParamsParser.OffsetMasterVol && p.Length >= 4)
        {
            int ch = (off - BulkParamsParser.OffsetPreamp) / 4;
            float v = BitConverter.ToSingle(p, 0);
            if (ch == 0) { _inputPreampLDb = v; OnPropertyChanged(nameof(InputPreampLDb)); }
            else if (ch == 1) { _inputPreampRDb = v; OnPropertyChanged(nameof(InputPreampRDb)); }
            else changed = false;
        }
        // Volume leveller (per-field).
        else if (off >= BulkParamsParser.OffsetLeveller && off < BulkParamsParser.OffsetPreamp)
        {
            ApplyLevellerField(off - BulkParamsParser.OffsetLeveller, p);
        }
        // Output channels: enabled / mute / gain / delay per output slot.
        else if (off >= BulkParamsParser.OffsetOutputs && off < BulkParamsParser.OffsetPinConfig)
        {
            ApplyOutputField(off - BulkParamsParser.OffsetOutputs, p);
        }
        // Per-channel delays.
        else if (off >= BulkParamsParser.OffsetDelays && off < BulkParamsParser.OffsetCrosspoints && p.Length >= 4)
        {
            int wireCh = (off - BulkParamsParser.OffsetDelays) / 4;
            int appId = ChannelMap.WireToApp(wireCh, NumInputChannels);
            if (appId >= 0)
            {
                _channelDelays[appId] = BitConverter.ToSingle(p, 0);
                if (appId >= 2 && appId <= 10) MatrixOutputDelayChanged?.Invoke(appId - 2);
            }
            else changed = false;
        }
        else changed = false;

        if (changed) CheckDirty();
    }

    private void ApplyLevellerField(int sub, byte[] p)
    {
        switch (sub)
        {
            case 0 when p.Length >= 1: _levellerEnabled = p[0] != 0; OnPropertyChanged(nameof(LevellerEnabled)); break;
            case 1 when p.Length >= 1: _levellerSpeed = p[0]; OnPropertyChanged(nameof(LevellerSpeed)); break;
            case 2 when p.Length >= 1: _levellerLookahead = p[0] != 0; OnPropertyChanged(nameof(LevellerLookahead)); break;
            case 4 when p.Length >= 4: _levellerAmount = BitConverter.ToSingle(p, 0); OnPropertyChanged(nameof(LevellerAmount)); break;
            case 8 when p.Length >= 4: _levellerMaxGainDb = BitConverter.ToSingle(p, 0); OnPropertyChanged(nameof(LevellerMaxGainDb)); break;
            case 12 when p.Length >= 4: _levellerGateDb = BitConverter.ToSingle(p, 0); OnPropertyChanged(nameof(LevellerGateDb)); break;
            case 16 when p.Length >= 1: _levellerDetectorMask = p[0]; OnPropertyChanged(nameof(LevellerDetectorMask)); break;
            case 17 when p.Length >= 1: _levellerApplyMask = p[0]; OnPropertyChanged(nameof(LevellerApplyMask)); break;
        }
    }

    private void ApplyOutputField(int rel, byte[] p)
    {
        int outIdx = rel / 12;
        int sub = rel % 12;
        int appId = 2 + outIdx;   // outputs occupy app ids 2..10
        switch (sub)
        {
            case 0 when p.Length >= 1:   // enabled
                bool en = p[0] != 0;
                _outputEnabled[outIdx] = en;
                OutputEnabledChanged?.Invoke(outIdx, en);
                break;
            case 1 when p.Length >= 1:   // mute
                _channelMutes[appId] = p[0] != 0;
                MatrixOutputMuteChanged?.Invoke(outIdx);
                break;
            case 4 when p.Length >= 4:   // gain
                _channelGains[appId] = BitConverter.ToSingle(p, 0);
                MatrixOutputGainChanged?.Invoke(outIdx);
                break;
            case 8 when p.Length >= 4:   // delay
                _channelDelays[appId] = BitConverter.ToSingle(p, 0);
                MatrixOutputDelayChanged?.Invoke(outIdx);
                break;
        }
    }
}
