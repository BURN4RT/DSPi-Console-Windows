using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DSPiConsole.Core;
using DSPiConsole.Core.Models;
using DSPiConsole.Usb;
using Microsoft.UI.Dispatching;

namespace DSPiConsole.ViewModels;

/// <summary>
/// Main ViewModel for the DSPi Console application.
/// Manages all DSP state, USB communication, and UI bindings.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DspDevice _device;
    private readonly DispatcherQueue _dispatcher;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;

    // Channel filter data: Dictionary<ChannelId, List<FilterParams>>
    private readonly Dictionary<int, ObservableCollection<FilterParams>> _channelData = new();
    
    // Channel visibility for graph: Dictionary<ChannelId, bool>
    private readonly Dictionary<int, bool> _channelVisibility = new();
    
    // Channel delays: Dictionary<ChannelId, float>
    private readonly Dictionary<int, float> _channelDelays = new();

    // Channel gains: Dictionary<ChannelId, float> (output channels only)
    private readonly Dictionary<int, float> _channelGains = new();

    // Channel mutes: Dictionary<ChannelId, bool> (output channels only)
    private readonly Dictionary<int, bool> _channelMutes = new();

    // Output enabled in matrix mixer: Dictionary<outputIndex, bool>
    private readonly Dictionary<int, bool> _outputEnabled = new();

    // Matrix mixer state: [inputIndex, outputIndex]
    private readonly bool[,] _matrixRouting = new bool[2, 9];
    private readonly float[,] _matrixGain = new float[2, 9];
    private readonly bool[,] _matrixInvert = new bool[2, 9];

    // Per-output matrix mixer state (indexed by output position in ActiveOutputs)
    private readonly bool[] _outputMuted = new bool[9];

    // Output pin assignments: Dictionary<pinOutputId, byte>
    private readonly Dictionary<int, byte> _outputPins = new();

    // Clip tracking
    private ushort _clipLatched;
    private DateTime? _clipTimestamp;

    // Preset system state
    private int _activePresetSlot = -1;
    private ushort _presetOccupiedMask;
    private readonly string[] _presetNames = new string[10];
    private byte _presetStartupMode;
    private byte _presetDefaultSlot;
    private bool _presetIncludePins;
    private PresetSnapshot? _savedSnapshot;

    [ObservableProperty]
    private float _preampDb;

    [ObservableProperty]
    private bool _bypass;

    [ObservableProperty]
    private bool _isDeviceConnected;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private SystemStatus _status = new();

    [ObservableProperty]
    private Channel? _selectedChannel;

    [ObservableProperty]
    private bool _loudnessEnabled;

    [ObservableProperty]
    private float _loudnessRefSPL = 83.0f;

    [ObservableProperty]
    private float _loudnessIntensity = 100.0f;

    [ObservableProperty]
    private bool _crossfeedEnabled;

    [ObservableProperty]
    private int _crossfeedPreset = 0; // 0=Default, 1=Chu Moy, 2=Jan Meier, 3=Custom

    [ObservableProperty]
    private float _crossfeedFreq = 700.0f; // Hz (500-2000)

    [ObservableProperty]
    private float _crossfeedFeed = 4.5f; // dB (0-15)

    [ObservableProperty]
    private bool _crossfeedItd = true; // Inter-aural Time Delay

    [ObservableProperty]
    private int _activePreset = -1;

    [ObservableProperty]
    private bool _presetsDirty;

    [ObservableProperty]
    private string _platform = "";

    public IReadOnlyList<Channel> ActiveOutputs => Platform switch
    {
        "RP2040" => Channel.Rp2040Outputs,
        "RP2350" => Channel.Outputs,
        _        => Array.Empty<Channel>()
    };

    public event EventHandler? ActiveOutputsChanged;

    // Preset events and accessors
    public event EventHandler? PresetsChanged;
    public const int PresetSlotCount = 10;

    public bool IsPresetOccupied(int slot) => (_presetOccupiedMask & (1 << slot)) != 0;
    public ushort PresetOccupiedMask => _presetOccupiedMask;
    public string GetPresetName(int slot) => !string.IsNullOrEmpty(_presetNames[slot]) ? _presetNames[slot] : $"Preset {slot + 1}";
    public string GetPresetDisplayName(int slot) => IsPresetOccupied(slot) ? GetPresetName(slot) : $"Preset {slot + 1} (empty)";
    public byte PresetStartupMode => _presetStartupMode;
    public byte PresetDefaultSlot => _presetDefaultSlot;
    public bool PresetIncludePins => _presetIncludePins;

    partial void OnPlatformChanged(string value)
    {
        _outputEnabled.Clear();
        ActiveOutputsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── PDM / EQ-worker conflict helpers ──

    public int PdmOutputIndex => Platform == "RP2040" ? 4 : 8;
    private int EqWorkerStart => 2;
    private int EqWorkerEnd => Platform == "RP2040" ? 4 : 8; // exclusive

    public bool WouldConflict(int outputIndex)
    {
        if (outputIndex == PdmOutputIndex)
        {
            for (int i = EqWorkerStart; i < EqWorkerEnd; i++)
                if (IsOutputEnabled(i)) return true;
            return false;
        }
        if (outputIndex >= EqWorkerStart && outputIndex < EqWorkerEnd)
            return IsOutputEnabled(PdmOutputIndex);
        return false;
    }

    public async Task SwitchToPdmAsync()
    {
        await Task.Run(() =>
        {
            for (int i = EqWorkerStart; i < EqWorkerEnd; i++)
                _device.SetOutputEnable(i, false);
            _device.SetOutputEnable(PdmOutputIndex, true);
            for (int i = EqWorkerStart; i < EqWorkerEnd; i++)
                FetchOutputEnable(i);
            FetchOutputEnable(PdmOutputIndex);
        });
    }

    public async Task SwitchFromPdmAsync(int enabling)
    {
        await Task.Run(() =>
        {
            _device.SetOutputEnable(PdmOutputIndex, false);
            _device.SetOutputEnable(enabling, true);
            FetchOutputEnable(PdmOutputIndex);
            FetchOutputEnable(enabling);
        });
    }

    public IReadOnlyDictionary<int, ObservableCollection<FilterParams>> ChannelData => _channelData;
    public IReadOnlyDictionary<int, bool> ChannelVisibility => _channelVisibility;
    public IReadOnlyDictionary<int, float> ChannelDelays => _channelDelays;
    public IReadOnlyDictionary<int, float> ChannelGains => _channelGains;
    public IReadOnlyDictionary<int, bool> ChannelMutes => _channelMutes;

    public DspDevice Device => _device;

    // Channel name overrides: Dictionary<ChannelId, string>
    private readonly Dictionary<int, string> _channelNames = new();

    public string GetChannelName(Channel channel) =>
        _channelNames.TryGetValue((int)channel.Id, out var n) ? n : channel.Name;

    public void SetChannelName(Channel channel, string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name) || name == GetChannelName(channel)) return;
        _channelNames[(int)channel.Id] = name;
        Task.Run(() => _device.SetChannelNameOnDevice((int)channel.Id, name));
        ChannelNameChanged?.Invoke((int)channel.Id);
        PresetsDirty = true;
    }

    // Output enabled state for matrix mixer / sidebar filtering
    public bool IsOutputEnabled(int outputIndex) =>
        _outputEnabled.TryGetValue(outputIndex, out var v) && v;

    public void SetOutputEnabled(int outputIndex, bool enabled)
    {
        _outputEnabled[outputIndex] = enabled;
        OutputEnabledChanged?.Invoke(outputIndex, enabled);
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public event Action<int, bool>? OutputEnabledChanged;

    // Matrix mixer change events
    public event Action<int, int>? MatrixRouteChanged;   // (input, output)
    public event Action<int>? MatrixOutputGainChanged;    // (outputIndex)
    public event Action<int>? MatrixOutputMuteChanged;    // (outputIndex)
    public event Action<int>? MatrixOutputDelayChanged;   // (outputIndex)

    // Event for notifying UI when graph needs redraw
    public event Action<int>? ChannelNameChanged;
    public event EventHandler? FiltersChanged;
    public event EventHandler? VisibilityChanged;

    public MainViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _device = new DspDevice();

        // Initialize channel data
        foreach (var channel in Channel.All)
        {
            var filters = new ObservableCollection<FilterParams>();
            for (int i = 0; i < channel.BandCount; i++)
            {
                filters.Add(new FilterParams());
            }
            _channelData[(int)channel.Id] = filters;
            _channelVisibility[(int)channel.Id] = true;
            _channelDelays[(int)channel.Id] = 0.0f;
            if (channel.IsOutput)
            {
                _channelGains[(int)channel.Id] = 0.0f;
                _channelMutes[(int)channel.Id] = false;
            }
        }

        // Subscribe to device events
        _device.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(DspDevice.IsConnected))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    IsDeviceConnected = _device.IsConnected;
                    if (_device.IsConnected)
                    {
                        Task.Run(() =>
                        {
                            var info = _device.GetDeviceInfo();
                            var newPlatform = info?.Platform ?? "";
                            // Set channel count for platform-aware status parsing
                            _device.NumChannels = newPlatform == "RP2350" ? 11 : 7;
                            _dispatcher.TryEnqueue(() => Platform = newPlatform);
                            System.Threading.Thread.Sleep(100);
                            FetchAll();
                            FetchPresetInfo();
                            _dispatcher.TryEnqueue(() => UpdateSavedSnapshot());
                        });
                    }
                    else
                    {
                        // Keep Platform so the UI layout stays until a new device connects
                        ResetChannelData();
                        _presetsChecked = false;
                        ActivePreset = -1;
                        PresetsDirty = false;
                    }
                });
            }
            else if (e.PropertyName == nameof(DspDevice.ErrorMessage))
            {
                _dispatcher.TryEnqueue(() => ErrorMessage = _device.ErrorMessage);
            }
        };

        // Status polling timer (60ms interval)
        _pollTimer = new System.Timers.Timer(60);
        _pollTimer.Elapsed += (s, e) =>
        {
            if (IsDeviceConnected)
            {
                FetchStatus();
            }
        };
        _pollTimer.AutoReset = true;

        // Start device monitoring
        _device.StartMonitoring();
        _pollTimer.Start();
    }

    private void ResetChannelData()
    {
        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            if (_channelData.TryGetValue(id, out var filters))
            {
                for (int i = 0; i < filters.Count; i++)
                    filters[i] = new FilterParams();
            }
            _channelDelays[id] = 0.0f;
            if (channel.IsOutput)
            {
                _channelGains[id] = 0.0f;
                _channelMutes[id] = false;
            }
        }
        PreampDb = 0;
        Bypass = false;
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateChannelSelection(Channel? channel)
    {
        SelectedChannel = channel;

        if (channel != null)
        {
            // Show only selected channel
            foreach (var ch in Channel.All)
            {
                _channelVisibility[(int)ch.Id] = ch.Id == channel.Id;
            }
        }
        else
        {
            // Show all channels
            foreach (var ch in Channel.All)
            {
                _channelVisibility[(int)ch.Id] = true;
            }
        }

        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleChannelVisibility(Channel channel)
    {
        var id = (int)channel.Id;
        _channelVisibility[id] = !_channelVisibility[id];
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool GetChannelVisibility(Channel channel)
    {
        if (!_channelVisibility.TryGetValue((int)channel.Id, out var v) || !v)
            return false;
        if (channel.IsOutput)
        {
            int outputIndex = GetOutputIndex((int)channel.Id);
            if (outputIndex >= 0 && !IsOutputEnabled(outputIndex))
                return false;
        }
        return true;
    }

    public float GetChannelDelay(Channel channel) =>
        _channelDelays.TryGetValue((int)channel.Id, out var d) ? d : 0;

    public float GetChannelGain(Channel channel) =>
        _channelGains.TryGetValue((int)channel.Id, out var g) ? g : 0;

    public bool GetChannelMute(Channel channel) =>
        _channelMutes.TryGetValue((int)channel.Id, out var m) && m;

    public ObservableCollection<FilterParams> GetFilters(Channel channel) =>
        _channelData.TryGetValue((int)channel.Id, out var f) ? f : new();

    // ── Matrix mixer accessors ──

    public bool GetMatrixRouting(int input, int output) => _matrixRouting[input, output];
    public float GetMatrixGain(int input, int output) => _matrixGain[input, output];
    public bool GetMatrixInvert(int input, int output) => _matrixInvert[input, output];
    public float GetOutputGainDb(int output)
    {
        var outputs = ActiveOutputs;
        if (output < 0 || output >= outputs.Count) return 0f;
        return _channelGains.TryGetValue((int)outputs[output].Id, out var g) ? g : 0f;
    }
    public bool GetOutputMuted(int output) =>
        output >= 0 && output < _outputMuted.Length && _outputMuted[output];
    public float GetOutputDelayMs(int output)
    {
        var outputs = ActiveOutputs;
        if (output < 0 || output >= outputs.Count) return 0f;
        return _channelDelays.TryGetValue((int)outputs[output].Id, out var d) ? d : 0f;
    }

    public void SetMatrixRoute(int input, int output, bool enabled, float gain, bool invert)
    {
        _matrixRouting[input, output] = enabled;
        _matrixGain[input, output] = gain;
        _matrixInvert[input, output] = invert;
        Task.Run(() => _device.SetMatrixRoute(input, output, enabled, invert, gain));
        MatrixRouteChanged?.Invoke(input, output);
        PresetsDirty = true;
    }

    public void SetOutputGainDb(int output, float db)
    {
        var outputs = ActiveOutputs;
        if (output < 0 || output >= outputs.Count) return;
        int channelId = (int)outputs[output].Id;
        SetChannelGain(channelId, db);
    }

    public void SetOutputMuted(int output, bool muted)
    {
        if (output < 0 || output >= _outputMuted.Length) return;
        _outputMuted[output] = muted;
        Task.Run(() => _device.SetOutputMute(output, muted));
        MatrixOutputMuteChanged?.Invoke(output);
        PresetsDirty = true;
    }

    public void SetOutputDelayMs(int output, float ms)
    {
        var outputs = ActiveOutputs;
        if (output < 0 || output >= outputs.Count) return;
        int channelId = (int)outputs[output].Id;
        SetDelay(channelId, ms);
    }

    public void SetOutputEnableUsb(int output, bool enabled)
    {
        Task.Run(() => _device.SetOutputEnable(output, enabled));
        PresetsDirty = true;
    }

    // ── Pin assignment accessors ──

    public byte GetOutputPinValue(int pinOutputId) =>
        _outputPins.TryGetValue(pinOutputId, out var v) ? v : (byte)0;

    public void FetchOutputPin(int pinOutputId)
    {
        var pin = _device.GetOutputPin(pinOutputId);
        if (pin.HasValue)
            _outputPins[pinOutputId] = pin.Value;
    }

    public byte SetOutputPinValue(int pinOutputId, byte pin)
    {
        var status = _device.SetOutputPin(pinOutputId, pin);
        if (status == Usb.PinConfigResult.Success)
            _outputPins[pinOutputId] = pin;
        return status;
    }

    #region USB Commands

    private void FetchAll()
    {
        try
        {
            // Try bulk fetch first (firmware v2+ with 0xA0 support)
            var bulk = _device.GetAllParams();
            if (bulk != null)
            {
                var parsed = BulkParamsParser.Parse(bulk);
                if (parsed != null)
                {
                    ApplyBulkParams(parsed);
                    return;
                }
            }

            // Fallback to legacy per-command fetching
            FetchAllLegacy();
        }
        catch { }
    }

    private void ApplyBulkParams(BulkParams bp)
    {
        var outputs = ActiveOutputs;

        // EQ bands — apply first BandCount bands per channel
        foreach (var channel in Channel.All)
        {
            int ch = (int)channel.Id;
            if (_channelData.TryGetValue(ch, out var filters))
            {
                for (int band = 0; band < channel.BandCount && band < bp.MaxBands; band++)
                {
                    var fp = bp.Eq[ch, band];
                    int b = band; // capture for closure
                    if (!filters[b].Equals(fp))
                        _dispatcher.TryEnqueue(() => filters[b] = fp);
                }
            }
        }

        // Output channel gains, mutes, delays, and enable states
        for (int o = 0; o < outputs.Count && o < bp.Outputs.Length; o++)
        {
            var (enabled, muted, gain, delay) = bp.Outputs[o];
            int channelId = (int)outputs[o].Id;

            _channelGains[channelId] = gain;
            _channelMutes[channelId] = muted;
            _channelDelays[channelId] = delay;
            _outputEnabled[o] = enabled;
            if (o < _outputMuted.Length)
                _outputMuted[o] = muted;
        }

        // Matrix crosspoints
        for (int inp = 0; inp < 2; inp++)
        {
            for (int o = 0; o < outputs.Count && o < 9; o++)
            {
                var (enabled, invert, gain) = bp.Crosspoints[inp, o];
                _matrixRouting[inp, o] = enabled;
                _matrixInvert[inp, o] = invert;
                _matrixGain[inp, o] = gain;
            }
        }

        // Pin assignments
        for (int i = 0; i < bp.Pins.Length; i++)
            _outputPins[i] = bp.Pins[i];

        // Channel names
        for (int ch = 0; ch < bp.ChannelNames.Length; ch++)
        {
            var name = bp.ChannelNames[ch];
            if (!string.IsNullOrEmpty(name))
                _channelNames[ch] = name;
        }

        // Dispatch all UI updates
        _dispatcher.TryEnqueue(() =>
        {
            PreampDb = bp.PreampGainDb;
            Bypass = bp.Bypass;
            LoudnessEnabled = bp.LoudnessEnabled;
            LoudnessRefSPL = bp.LoudnessRefSpl;
            LoudnessIntensity = bp.LoudnessIntensityPct;
            CrossfeedEnabled = bp.CrossfeedEnabled;
            CrossfeedPreset = bp.CrossfeedPreset;
            CrossfeedItd = bp.CrossfeedItd;
            CrossfeedFreq = bp.CrossfeedFreq;
            CrossfeedFeed = bp.CrossfeedFeedDb;

            OnPropertyChanged(nameof(ChannelDelays));
            OnPropertyChanged(nameof(ChannelGains));
            OnPropertyChanged(nameof(ChannelMutes));

            for (int o = 0; o < outputs.Count; o++)
            {
                OutputEnabledChanged?.Invoke(o, _outputEnabled.TryGetValue(o, out var v) && v);
                MatrixOutputGainChanged?.Invoke(o);
                MatrixOutputMuteChanged?.Invoke(o);
                MatrixOutputDelayChanged?.Invoke(o);
                for (int inp = 0; inp < 2; inp++)
                    MatrixRouteChanged?.Invoke(inp, o);
            }

            for (int ch = 0; ch < bp.ChannelNames.Length; ch++)
            {
                if (!string.IsNullOrEmpty(bp.ChannelNames[ch]))
                    ChannelNameChanged?.Invoke(ch);
            }

            VisibilityChanged?.Invoke(this, EventArgs.Empty);
            FiltersChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void FetchAllLegacy()
    {
        if (!FetchPreamp()) return;
        FetchBypass();

        foreach (var channel in Channel.All)
        {
            for (int band = 0; band < channel.BandCount; band++)
            {
                FetchFilter((int)channel.Id, band);
            }
        }

        foreach (var channel in ActiveOutputs)
        {
            FetchDelay((int)channel.Id);
            FetchChannelGain((int)channel.Id);
            FetchChannelMute((int)channel.Id);
        }

        FetchLoudness();
        FetchCrossfeed();

        // Fetch matrix mixer state
        FetchMatrixRoutes();
        var outputCount = ActiveOutputs.Count;
        for (int o = 0; o < outputCount; o++)
        {
            FetchOutputEnable(o);
            FetchOutputMuteState(o);
        }

        // Fetch pin assignments
        int pinCount = Platform == "RP2350" ? 5 : 3;
        for (int p = 0; p < pinCount; p++)
            FetchOutputPin(p);

        // Dispatch FiltersChanged to run after all filter updates are processed
        _dispatcher.TryEnqueue(() => FiltersChanged?.Invoke(this, EventArgs.Empty));
    }

    private void FetchStatus()
    {
        try
        {
            var status = _device.GetStatus();
            if (status != null)
            {
                // Clip latching: OR incoming clip flags into latched state
                ushort newBits = (ushort)(status.ClipFlags & ~_clipLatched);
                if (newBits != 0)
                {
                    _clipLatched |= status.ClipFlags;
                    _clipTimestamp = DateTime.UtcNow;
                }

                // Auto-clear after 10 seconds of no new clips
                if (_clipLatched != 0 && _clipTimestamp.HasValue &&
                    (DateTime.UtcNow - _clipTimestamp.Value).TotalSeconds > 10)
                {
                    _clipLatched = 0;
                    _clipTimestamp = null;
                    try { _device.ClearClips(); } catch { }
                }

                status.ClipLatched = _clipLatched;
                status.ClipTimestamp = _clipTimestamp;

                _dispatcher.TryEnqueue(() => Status = status);
            }
        }
        catch { }
    }

    public void SetFilter(int channel, int band, FilterParams p)
    {
        if (_channelData.TryGetValue(channel, out var filters) && band < filters.Count)
        {
            filters[band] = p;
        }
        Task.Run(() => _device.SetFilter(channel, band, p));
        FiltersChanged?.Invoke(this, EventArgs.Empty);
        PresetsDirty = true;
    }

    private void FetchFilter(int channel, int band)
    {
        var p = _device.GetFilter(channel, band);
        if (p != null && _channelData.TryGetValue(channel, out var filters) && band < filters.Count)
        {
            if (!filters[band].Equals(p))
            {
                _dispatcher.TryEnqueue(() => filters[band] = p);
            }
        }
    }

    public void SetDelay(int channel, float ms)
    {
        _channelDelays[channel] = ms;
        int outputIndex = GetOutputIndex(channel);
        if (outputIndex >= 0)
            Task.Run(() => _device.SetOutputDelay(outputIndex, ms));
        OnPropertyChanged(nameof(ChannelDelays));
        if (outputIndex >= 0)
            MatrixOutputDelayChanged?.Invoke(outputIndex);
        PresetsDirty = true;
    }

    private void FetchDelay(int channel)
    {
        int outputIndex = GetOutputIndex(channel);
        if (outputIndex < 0) return;
        var delay = _device.GetOutputDelay(outputIndex);
        if (delay.HasValue)
        {
            var current = _channelDelays.TryGetValue(channel, out var d) ? d : 0;
            if (Math.Abs(current - delay.Value) > 0.01f)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelDelays[channel] = delay.Value;
                    OnPropertyChanged(nameof(ChannelDelays));
                    MatrixOutputDelayChanged?.Invoke(outputIndex);
                });
            }
        }
    }

    private int GetOutputIndex(int channelId)
    {
        var outputs = ActiveOutputs;
        for (int i = 0; i < outputs.Count; i++)
            if ((int)outputs[i].Id == channelId) return i;
        return -1;
    }

    public void SetChannelGain(int channelId, float db)
    {
        _channelGains[channelId] = db;
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        Task.Run(() => _device.SetOutputGain(outputIndex, db));
        OnPropertyChanged(nameof(ChannelGains));
        MatrixOutputGainChanged?.Invoke(outputIndex);
        PresetsDirty = true;
    }

    private void FetchChannelGain(int channelId)
    {
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        var gain = _device.GetOutputGain(outputIndex);
        if (gain.HasValue)
        {
            var current = _channelGains.TryGetValue(channelId, out var g) ? g : 0;
            if (Math.Abs(current - gain.Value) > 0.01f)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelGains[channelId] = gain.Value;
                    OnPropertyChanged(nameof(ChannelGains));
                    MatrixOutputGainChanged?.Invoke(outputIndex);
                });
            }
        }
    }

    public void SetChannelMute(int channelId, bool muted)
    {
        _channelMutes[channelId] = muted;
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        Task.Run(() => _device.SetOutputMute(outputIndex, muted));
        OnPropertyChanged(nameof(ChannelMutes));
        PresetsDirty = true;
    }

    private void FetchChannelMute(int channelId)
    {
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        var muted = _device.GetOutputMute(outputIndex);
        if (muted.HasValue)
        {
            var current = _channelMutes.TryGetValue(channelId, out var m) && m;
            if (current != muted.Value)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelMutes[channelId] = muted.Value;
                    OnPropertyChanged(nameof(ChannelMutes));
                });
            }
        }
    }

    private void FetchLoudness()
    {
        var enabled = _device.GetLoudnessEnabled();
        if (enabled.HasValue)
            _dispatcher.TryEnqueue(() => LoudnessEnabled = enabled.Value);

        var refSpl = _device.GetLoudnessRefSPL();
        if (refSpl.HasValue)
            _dispatcher.TryEnqueue(() => LoudnessRefSPL = refSpl.Value);

        var intensity = _device.GetLoudnessIntensity();
        if (intensity.HasValue)
            _dispatcher.TryEnqueue(() => LoudnessIntensity = intensity.Value);
    }

    private void FetchCrossfeed()
    {
        var enabled = _device.GetCrossfeedEnabled();
        if (enabled.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedEnabled = enabled.Value);

        var preset = _device.GetCrossfeedPreset();
        if (preset.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedPreset = preset.Value);

        var freq = _device.GetCrossfeedFreq();
        if (freq.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedFreq = freq.Value);

        var feed = _device.GetCrossfeedFeed();
        if (feed.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedFeed = feed.Value);

        var itd = _device.GetCrossfeedItd();
        if (itd.HasValue)
            _dispatcher.TryEnqueue(() => CrossfeedItd = itd.Value);
    }

    private void FetchMatrixRoutes()
    {
        var outputs = ActiveOutputs;
        for (int input = 0; input < 2; input++)
        {
            for (int o = 0; o < outputs.Count; o++)
            {
                var route = _device.GetMatrixRoute(input, o);
                if (route.HasValue)
                {
                    _matrixRouting[input, o] = route.Value.enabled;
                    _matrixInvert[input, o] = route.Value.invert;
                    _matrixGain[input, o] = route.Value.gain;
                }
            }
        }
    }

    private void FetchOutputEnable(int output)
    {
        var enabled = _device.GetOutputEnable(output);
        if (enabled.HasValue)
        {
            _outputEnabled[output] = enabled.Value;
            _dispatcher.TryEnqueue(() =>
            {
                OutputEnabledChanged?.Invoke(output, enabled.Value);
                VisibilityChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private void FetchOutputMuteState(int output)
    {
        if (output < 0 || output >= _outputMuted.Length) return;
        var muted = _device.GetOutputMute(output);
        if (muted.HasValue)
            _outputMuted[output] = muted.Value;
    }

    partial void OnLoudnessEnabledChanged(bool value)
    {
        Task.Run(() => _device.SetLoudnessEnabled(value));
        PresetsDirty = true;
    }

    partial void OnLoudnessRefSPLChanged(float value)
    {
        Task.Run(() => _device.SetLoudnessRefSPL(value));
        PresetsDirty = true;
    }

    partial void OnLoudnessIntensityChanged(float value)
    {
        Task.Run(() => _device.SetLoudnessIntensity(value));
        PresetsDirty = true;
    }

    partial void OnCrossfeedEnabledChanged(bool value)
    {
        Task.Run(() => _device.SetCrossfeedEnabled(value));
        PresetsDirty = true;
    }

    partial void OnCrossfeedPresetChanged(int value)
    {
        Task.Run(() => _device.SetCrossfeedPreset(value));
        PresetsDirty = true;
    }

    partial void OnCrossfeedFreqChanged(float value)
    {
        Task.Run(() => _device.SetCrossfeedFreq(value));
        PresetsDirty = true;
    }

    partial void OnCrossfeedFeedChanged(float value)
    {
        Task.Run(() => _device.SetCrossfeedFeed(value));
        PresetsDirty = true;
    }

    partial void OnCrossfeedItdChanged(bool value)
    {
        Task.Run(() => _device.SetCrossfeedItd(value));
        PresetsDirty = true;
    }

    partial void OnPreampDbChanged(float value)
    {
        Task.Run(() => _device.SetPreamp(value));
        PresetsDirty = true;
    }

    private bool FetchPreamp()
    {
        var preamp = _device.GetPreamp();
        if (preamp.HasValue)
        {
            if (Math.Abs(PreampDb - preamp.Value) > 0.1f)
            {
                _dispatcher.TryEnqueue(() => PreampDb = preamp.Value);
            }
            return true;
        }
        _dispatcher.TryEnqueue(() => IsDeviceConnected = false);
        return false;
    }

    partial void OnBypassChanged(bool value)
    {
        Task.Run(() => _device.SetBypass(value));
        FiltersChanged?.Invoke(this, EventArgs.Empty);
        PresetsDirty = true;
    }

    private void FetchBypass()
    {
        var bypass = _device.GetBypass();
        if (bypass.HasValue)
        {
            _dispatcher.TryEnqueue(() => Bypass = bypass.Value);
        }
    }

    [RelayCommand]
    private void ClearAllMaster()
    {
        var defaultFilter = new FilterParams(FilterType.Flat, 1000, 0.707f, 0);
        var masterChannels = new[] { (int)ChannelId.MasterLeft, (int)ChannelId.MasterRight };

        foreach (var ch in masterChannels)
        {
            if (_channelData.TryGetValue(ch, out var filters))
            {
                for (int b = 0; b < filters.Count; b++)
                {
                    SetFilter(ch, b, defaultFilter.Clone());
                }
            }
        }
    }

    [RelayCommand]
    private void Reconnect()
    {
        Task.Run(() => _device.Reconnect());
    }

    /// <summary>
    /// Save current parameters to device flash.
    /// </summary>
    public async Task<byte> SaveParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        return await Task.Run(() =>
        {
            var result = _device.SaveParams();
            if (result == FlashResult.Ok)
                _dispatcher.TryEnqueue(() => { PresetsDirty = false; UpdateSavedSnapshot(); });
            return result;
        });
    }

    /// <summary>
    /// Load parameters from device flash, refreshing UI.
    /// </summary>
    public async Task<byte> LoadParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        return await Task.Run(() =>
        {
            var result = _device.LoadParams();
            if (result == FlashResult.Ok)
            {
                FetchAll();
                _dispatcher.TryEnqueue(() => { PresetsDirty = false; UpdateSavedSnapshot(); });
            }
            return result;
        });
    }

    /// <summary>
    /// Reset all parameters to factory defaults, refreshing UI.
    /// </summary>
    public async Task<byte> FactoryResetParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        return await Task.Run(() =>
        {
            var result = _device.FactoryReset();
            if (result == FlashResult.Ok)
            {
                FetchAll();
                _dispatcher.TryEnqueue(() => { PresetsDirty = false; UpdateSavedSnapshot(); });
            }
            return result;
        });
    }

    #endregion

    #region Preset Operations

    /// <summary>
    /// Fetch preset metadata from the device: occupied mask, names, active slot, startup info.
    /// Called after FetchAll() in the connect flow.
    /// </summary>
    public void FetchPresetInfo()
    {
        try
        {
            _presetsChecked = true;

            // Fetch directory (occupied mask, startup config, active slot, include-pins)
            var dir = _device.GetPresetDirectory();
            if (dir != null)
            {
                _presetOccupiedMask = dir.Value.OccupiedMask;
                _presetStartupMode = dir.Value.StartupMode;
                _presetDefaultSlot = dir.Value.DefaultSlot;
                _activePresetSlot = dir.Value.LastActiveSlot == 0xFF ? -1 : dir.Value.LastActiveSlot;
                _presetIncludePins = dir.Value.IncludePins;
            }

            // Fetch names for occupied slots
            for (int i = 0; i < PresetSlotCount; i++)
            {
                if (IsPresetOccupied(i))
                {
                    var name = _device.GetPresetName(i);
                    _presetNames[i] = name ?? "";
                }
                else
                {
                    _presetNames[i] = "";
                }
            }

            _dispatcher.TryEnqueue(() =>
            {
                ActivePreset = _activePresetSlot;
                PresetsDirty = false;
                PresetsChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch { }
    }

    private void RefreshPresetMetadata()
    {
        var dir = _device.GetPresetDirectory();
        if (dir == null) return;

        _presetOccupiedMask = dir.Value.OccupiedMask;
        _presetStartupMode = dir.Value.StartupMode;
        _presetDefaultSlot = dir.Value.DefaultSlot;
        _activePresetSlot = dir.Value.LastActiveSlot == 0xFF ? -1 : dir.Value.LastActiveSlot;
        _presetIncludePins = dir.Value.IncludePins;

        for (int i = 0; i < PresetSlotCount; i++)
        {
            if (IsPresetOccupied(i))
            {
                var name = _device.GetPresetName(i);
                _presetNames[i] = name ?? "";
            }
            else
            {
                _presetNames[i] = "";
            }
        }

        _dispatcher.TryEnqueue(() =>
        {
            ActivePreset = _activePresetSlot;
            PresetsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Save current parameters to a preset slot, optionally setting a name.
    /// </summary>
    public async Task<byte> SavePreset(int slot, string? name)
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            if (!string.IsNullOrEmpty(name))
                _device.SetPresetName(slot, name);

            var result = _device.SavePreset(slot);
            if (result == PresetResult.Ok)
            {
                RefreshPresetMetadata();
                _dispatcher.TryEnqueue(() =>
                {
                    PresetsDirty = false;
                    UpdateSavedSnapshot();
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Load a preset slot, resync all parameters from device.
    /// </summary>
    public async Task<byte> LoadPreset(int slot)
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            var result = _device.LoadPreset(slot);
            if (result == PresetResult.Ok)
            {
                // Wait for firmware mute period to end before re-syncing
                System.Threading.Thread.Sleep(10);
                FetchAll();
                _activePresetSlot = slot;
                _dispatcher.TryEnqueue(() =>
                {
                    ActivePreset = slot;
                    PresetsDirty = false;
                    UpdateSavedSnapshot();
                    PresetsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            return result;
        });
    }

    /// <summary>
    /// Delete a preset slot.
    /// </summary>
    public async Task<byte> DeletePreset(int slot)
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            var result = _device.DeletePreset(slot);
            if (result == PresetResult.Ok)
            {
                RefreshPresetMetadata();
            }
            return result;
        });
    }

    /// <summary>
    /// Rename a preset slot.
    /// </summary>
    public async Task<bool> RenamePreset(int slot, string name)
    {
        if (!IsDeviceConnected) return false;
        return await Task.Run(() =>
        {
            var ok = _device.SetPresetName(slot, name);
            if (ok)
            {
                _presetNames[slot] = name;
                _dispatcher.TryEnqueue(() => PresetsChanged?.Invoke(this, EventArgs.Empty));
            }
            return ok;
        });
    }

    /// <summary>
    /// Clear all presets from flash.
    /// </summary>
    public async Task<byte> ClearAllPresets()
    {
        if (!IsDeviceConnected) return PresetResult.FlashWriteError;
        return await Task.Run(() =>
        {
            var result = _device.ClearAllPresets();
            if (result == PresetResult.Ok)
            {
                _presetOccupiedMask = 0;
                _activePresetSlot = -1;
                for (int i = 0; i < PresetSlotCount; i++)
                    _presetNames[i] = "";
                _dispatcher.TryEnqueue(() =>
                {
                    ActivePreset = -1;
                    PresetsDirty = false;
                    PresetsChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            return result;
        });
    }

    public async Task<bool> SetPresetStartup(byte mode, byte defaultSlot)
    {
        if (!IsDeviceConnected) return false;
        return await Task.Run(() =>
        {
            var ok = _device.SetPresetStartup(mode, defaultSlot);
            if (ok)
            {
                _presetStartupMode = mode;
                _presetDefaultSlot = defaultSlot;
            }
            return ok;
        });
    }

    public async Task<bool> SetPresetIncludePins(bool include)
    {
        if (!IsDeviceConnected) return false;
        return await Task.Run(() =>
        {
            var ok = _device.SetPresetIncludePins(include);
            if (ok) _presetIncludePins = include;
            return ok;
        });
    }

    /// <summary>
    /// Capture the current state as the "saved" baseline for change detection.
    /// </summary>
    public void UpdateSavedSnapshot()
    {
        _savedSnapshot = PresetSnapshot.Capture(this);
    }

    /// <summary>
    /// Get a human-readable summary of changes since the last save/load.
    /// Returns null if no snapshot is available or no changes detected.
    /// </summary>
    public string? GetChangeSummary()
    {
        if (_savedSnapshot == null) return null;
        var current = PresetSnapshot.Capture(this);
        var changes = PresetDiff.Diff(_savedSnapshot, current, this);
        return changes.Count > 0 ? PresetDiff.FormatSummary(changes) : null;
    }

    /// <summary>
    /// Whether the device firmware supports presets (GetPresetDirectory succeeded).
    /// </summary>
    public bool PresetsSupported => _presetsChecked;
    private bool _presetsChecked;

    #endregion

    #region Graph Data Generation

    public (float[] frequencies, float[] magnitudes) GetResponseCurve(Channel channel)
    {
        if (!_channelData.TryGetValue((int)channel.Id, out var filters))
            return (Array.Empty<float>(), Array.Empty<float>());

        // If master channel and bypass is on, return flat response
        if ((channel.Id == ChannelId.MasterLeft || channel.Id == ChannelId.MasterRight) && Bypass)
        {
            var freqs = new float[201];
            var mags = new float[201];
            for (int i = 0; i < 201; i++)
            {
                float pct = i / 200.0f;
                freqs[i] = MathF.Pow(10, MathF.Log10(20) + pct * (MathF.Log10(20000) - MathF.Log10(20)));
                mags[i] = 0;
            }
            return (freqs, mags);
        }

        return DspMath.GenerateResponseCurve(filters);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer.Stop();
        _pollTimer.Dispose();
        _device.Dispose();

        GC.SuppressFinalize(this);
    }
}
