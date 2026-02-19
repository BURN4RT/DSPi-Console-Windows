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
    private string _platform = "";

    public IReadOnlyList<Channel> ActiveOutputs => Platform switch
    {
        "RP2040" => Channel.Rp2040Outputs,
        "RP2350" => Channel.Outputs,
        _        => Array.Empty<Channel>()
    };

    public event EventHandler? ActiveOutputsChanged;

    partial void OnPlatformChanged(string value)
    {
        _outputEnabled.Clear();
        ActiveOutputsChanged?.Invoke(this, EventArgs.Empty);
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
        ChannelNameChanged?.Invoke((int)channel.Id);
    }

    // Output enabled state for matrix mixer / sidebar filtering
    public bool IsOutputEnabled(int outputIndex) =>
        _outputEnabled.TryGetValue(outputIndex, out var v) && v;

    public void SetOutputEnabled(int outputIndex, bool enabled)
    {
        _outputEnabled[outputIndex] = enabled;
        OutputEnabledChanged?.Invoke(outputIndex, enabled);
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
                            _dispatcher.TryEnqueue(() => Platform = newPlatform);
                            System.Threading.Thread.Sleep(100);
                            _dispatcher.TryEnqueue(FetchAll);
                        });
                    }
                    else
                    {
                        // Keep Platform so the UI layout stays until a new device connects
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

    public bool GetChannelVisibility(Channel channel) => 
        _channelVisibility.TryGetValue((int)channel.Id, out var v) && v;

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
        _device.SetMatrixRoute(input, output, enabled, invert, gain);
        MatrixRouteChanged?.Invoke(input, output);
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
        _device.SetOutputMute(output, muted);
        MatrixOutputMuteChanged?.Invoke(output);
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
        _device.SetOutputEnable(output, enabled);
    }

    #region USB Commands

    private void FetchAll()
    {
        try
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

            // Dispatch FiltersChanged to run after all filter updates are processed
            _dispatcher.TryEnqueue(() => FiltersChanged?.Invoke(this, EventArgs.Empty));
        }
        catch { }
    }

    private void FetchStatus()
    {
        try
        {
            var status = _device.GetStatus();
            if (status != null)
            {
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
        _device.SetFilter(channel, band, p);
        FiltersChanged?.Invoke(this, EventArgs.Empty);
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
            _device.SetOutputDelay(outputIndex, ms);
        OnPropertyChanged(nameof(ChannelDelays));
        if (outputIndex >= 0)
            MatrixOutputDelayChanged?.Invoke(outputIndex);
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
        _device.SetOutputGain(outputIndex, db);
        OnPropertyChanged(nameof(ChannelGains));
        MatrixOutputGainChanged?.Invoke(outputIndex);
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
        _device.SetOutputMute(outputIndex, muted);
        OnPropertyChanged(nameof(ChannelMutes));
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
            _dispatcher.TryEnqueue(() => OutputEnabledChanged?.Invoke(output, enabled.Value));
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
        _device.SetLoudnessEnabled(value);
    }

    partial void OnLoudnessRefSPLChanged(float value)
    {
        _device.SetLoudnessRefSPL(value);
    }

    partial void OnLoudnessIntensityChanged(float value)
    {
        _device.SetLoudnessIntensity(value);
    }

    partial void OnCrossfeedEnabledChanged(bool value)
    {
        _device.SetCrossfeedEnabled(value);
    }

    partial void OnCrossfeedPresetChanged(int value)
    {
        _device.SetCrossfeedPreset(value);
    }

    partial void OnCrossfeedFreqChanged(float value)
    {
        _device.SetCrossfeedFreq(value);
    }

    partial void OnCrossfeedFeedChanged(float value)
    {
        _device.SetCrossfeedFeed(value);
    }

    partial void OnCrossfeedItdChanged(bool value)
    {
        _device.SetCrossfeedItd(value);
    }

    partial void OnPreampDbChanged(float value)
    {
        _device.SetPreamp(value);
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
        _device.SetBypass(value);
        FiltersChanged?.Invoke(this, EventArgs.Empty);
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
        _device.Reconnect();
    }

    /// <summary>
    /// Save current parameters to device flash.
    /// </summary>
    public byte SaveParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        return _device.SaveParams();
    }

    /// <summary>
    /// Load parameters from device flash, refreshing UI.
    /// </summary>
    public byte LoadParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        var result = _device.LoadParams();
        if (result == FlashResult.Ok)
        {
            FetchAll();
        }
        return result;
    }

    /// <summary>
    /// Reset all parameters to factory defaults, refreshing UI.
    /// </summary>
    public byte FactoryResetParams()
    {
        if (!IsDeviceConnected) return FlashResult.ErrWrite;
        var result = _device.FactoryReset();
        if (result == FlashResult.Ok)
        {
            FetchAll();
        }
        return result;
    }

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
