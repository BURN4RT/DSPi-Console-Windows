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

    partial void OnPlatformChanged(string value) =>
        ActiveOutputsChanged?.Invoke(this, EventArgs.Empty);

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
                        Platform = "";
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

    #region USB Commands

    private void FetchAll()
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

        // Dispatch FiltersChanged to run after all filter updates are processed
        _dispatcher.TryEnqueue(() => FiltersChanged?.Invoke(this, EventArgs.Empty));
    }

    private void FetchStatus()
    {
        var status = _device.GetStatus();
        if (status != null)
        {
            _dispatcher.TryEnqueue(() => Status = status);
        }
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
        _device.SetDelay(channel, ms);
        OnPropertyChanged(nameof(ChannelDelays));
    }

    private void FetchDelay(int channel)
    {
        var delay = _device.GetDelay(channel);
        if (delay.HasValue)
        {
            var current = _channelDelays.TryGetValue(channel, out var d) ? d : 0;
            if (Math.Abs(current - delay.Value) > 0.01f)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelDelays[channel] = delay.Value;
                    OnPropertyChanged(nameof(ChannelDelays));
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
        _device.SetChannelGain(outputIndex, db);
        OnPropertyChanged(nameof(ChannelGains));
    }

    private void FetchChannelGain(int channelId)
    {
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        var gain = _device.GetChannelGain(outputIndex);
        if (gain.HasValue)
        {
            var current = _channelGains.TryGetValue(channelId, out var g) ? g : 0;
            if (Math.Abs(current - gain.Value) > 0.01f)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _channelGains[channelId] = gain.Value;
                    OnPropertyChanged(nameof(ChannelGains));
                });
            }
        }
    }

    public void SetChannelMute(int channelId, bool muted)
    {
        _channelMutes[channelId] = muted;
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        _device.SetChannelMute(outputIndex, muted);
        OnPropertyChanged(nameof(ChannelMutes));
    }

    private void FetchChannelMute(int channelId)
    {
        int outputIndex = GetOutputIndex(channelId);
        if (outputIndex < 0) return;
        var muted = _device.GetChannelMute(outputIndex);
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
