using System;
using Microsoft.UI.Dispatching;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DSPiConsole.Services;

/// <summary>
/// Bidirectional bridge between the sidebar volume slider (in "User Volume" mode
/// while the DSPi input source is USB) and the Windows system default render
/// endpoint via WASAPI / IAudioEndpointVolume.
///
/// Mirrors the macOS app's <c>HostVolumeController</c>:
///   • Targets whatever device is the current Windows default render endpoint
///     (i.e. the one the main system-tray volume slider drives). When DSPi is
///     selected as the playback device (the normal state when input source is
///     USB) this is automatically the DSPi endpoint, so the two sliders stay in
///     lockstep.
///   • Re-binds when the user changes the default playback device (system tray
///     device picker, hardware unplug, etc.) via
///     <see cref="IMMNotificationClient.OnDefaultDeviceChanged"/>.
///   • Subscribes to <see cref="AudioEndpointVolume.OnVolumeNotification"/> so
///     changes made anywhere — system-tray slider, hardware volume keys, other
///     apps, scripts — flow back into the app slider in real time.
///   • Slider drags push through <see cref="SetVolumeScalar(float)"/>; the
///     endpoint then echoes the change back through OnVolumeNotification, which
///     we suppress when the value matches what we just wrote (so we don't
///     fight an in-flight rapid drag).
///
/// All public events fire on the UI dispatcher.
/// </summary>
public sealed class HostVolumeService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private MMDeviceEnumerator? _enumerator;
    private NotificationClient? _notificationClient;
    private MMDevice? _device;
    private AudioEndpointVolume? _endpointVolume;
    private bool _disposed;

    // Scalar last written by SetVolumeScalar — used to drop the echo notification
    // the endpoint sends back when we are the one who changed the volume.
    private float _lastWrittenScalar = -1f;

    public HostVolumeService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>Bound to the current default render endpoint.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Current endpoint volume as a 0..1 scalar (Windows applies the
    /// perceptual taper internally). -1 when no endpoint is bound.</summary>
    public float VolumeScalar { get; private set; } = -1f;

    /// <summary>Cached dB for display. <see cref="float.NegativeInfinity"/> at
    /// scalar 0; otherwise read from the endpoint (falls back to 20·log10
    /// if the endpoint doesn't expose dB).</summary>
    public float VolumeDb { get; private set; } = float.NegativeInfinity;

    /// <summary>True if the current endpoint is muted.</summary>
    public bool IsMuted { get; private set; }

    /// <summary>Human-readable endpoint name (for tooltips / diagnostics).</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>Fires when scalar, dB, mute state, or availability change. Always
    /// raised on the UI dispatcher.</summary>
    public event EventHandler? VolumeChanged;

    /// <summary>Start tracking the system default render endpoint. Returns true
    /// on success — false on COM failure or no default endpoint configured.</summary>
    public bool Start()
    {
        if (_disposed) return false;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _notificationClient = new NotificationClient(this);
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
            BindToCurrentDefault();
            return true;
        }
        catch
        {
            // Common causes: no audio devices, COM init failure, MMDevice
            // service not running. Leave IsAvailable=false and the caller can
            // fall back to driving REQ_SET_USER_VOLUME (0xDA) directly.
            return false;
        }
    }

    /// <summary>Push a new scalar to the endpoint. No-op if no endpoint bound.</summary>
    public void SetVolumeScalar(float scalar)
    {
        if (!IsAvailable || _endpointVolume == null) return;
        var clamped = Math.Clamp(scalar, 0f, 1f);
        try
        {
            _lastWrittenScalar = clamped;
            _endpointVolume.MasterVolumeLevelScalar = clamped;
            // Update local cache immediately so the next OnVolumeNotification
            // sees a matching value and is correctly identified as our echo.
            VolumeScalar = clamped;
            VolumeDb = ScalarToDb(clamped, _endpointVolume);
        }
        catch { /* device disappeared mid-write; OnDefaultDeviceChanged will tidy up */ }
    }

    /// <summary>Push a dB value through after converting to scalar (Windows
    /// taper). Useful when the caller already thinks in dB.</summary>
    public void SetVolumeDb(float db)
    {
        if (!IsAvailable || _endpointVolume == null) return;
        try
        {
            // Prefer the endpoint's native dB→scalar conversion when available.
            // If the device doesn't expose a dB range, fall back to 10^(dB/20)
            // (the inverse of 20·log10).
            float scalar;
            try
            {
                _endpointVolume.MasterVolumeLevel = db;
                scalar = _endpointVolume.MasterVolumeLevelScalar;
            }
            catch
            {
                scalar = (float)Math.Pow(10.0, db / 20.0);
            }
            SetVolumeScalar(scalar);
        }
        catch { }
    }

    /// <summary>Toggle the endpoint mute. No-op if no endpoint bound.</summary>
    public void SetMute(bool muted)
    {
        if (!IsAvailable || _endpointVolume == null) return;
        try { _endpointVolume.Mute = muted; }
        catch { }
    }

    private void BindToCurrentDefault()
    {
        UnbindCurrentDevice();
        try
        {
            var dev = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var ev = dev.AudioEndpointVolume;
            _device = dev;
            _endpointVolume = ev;
            DeviceName = SafeFriendlyName(dev);
            VolumeScalar = ev.MasterVolumeLevelScalar;
            VolumeDb = ScalarToDb(VolumeScalar, ev);
            IsMuted = ev.Mute;
            ev.OnVolumeNotification += OnEndpointVolumeNotification;
            IsAvailable = true;
        }
        catch
        {
            IsAvailable = false;
            VolumeScalar = -1f;
            VolumeDb = float.NegativeInfinity;
            IsMuted = false;
            DeviceName = "";
        }
        RaiseChanged();
    }

    private void UnbindCurrentDevice()
    {
        if (_endpointVolume != null)
        {
            try { _endpointVolume.OnVolumeNotification -= OnEndpointVolumeNotification; }
            catch { }
            try { _endpointVolume.Dispose(); }
            catch { }
            _endpointVolume = null;
        }
        if (_device != null)
        {
            try { _device.Dispose(); } catch { }
            _device = null;
        }
        IsAvailable = false;
    }

    private void OnEndpointVolumeNotification(AudioVolumeNotificationData data)
    {
        // Echo suppression: if the new scalar matches what we just wrote (within
        // a small tolerance) we still update local cache but don't re-fire the
        // event — saves a no-op slider rebuild and matches macOS's listener
        // dampening pattern.
        var newScalar = data.MasterVolume;
        bool isEcho = Math.Abs(newScalar - _lastWrittenScalar) < 0.0005f;

        _dispatcher.TryEnqueue(() =>
        {
            VolumeScalar = newScalar;
            VolumeDb = _endpointVolume != null
                ? ScalarToDb(newScalar, _endpointVolume)
                : ScalarToDbFallback(newScalar);
            IsMuted = data.Muted;
            if (!isEcho) VolumeChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private static float ScalarToDb(float scalar, AudioEndpointVolume ev)
    {
        if (scalar <= 0f) return float.NegativeInfinity;
        try { return ev.MasterVolumeLevel; }
        catch { return ScalarToDbFallback(scalar); }
    }

    private static float ScalarToDbFallback(float scalar)
    {
        if (scalar <= 0f) return float.NegativeInfinity;
        return (float)(20.0 * Math.Log10(scalar));
    }

    private static string SafeFriendlyName(MMDevice dev)
    {
        try { return dev.FriendlyName ?? ""; }
        catch { return ""; }
    }

    private void RaiseChanged()
    {
        _dispatcher.TryEnqueue(() => VolumeChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Receives endpoint-enumeration notifications from MMDeviceEnumerator. We
    /// only care about default-device changes (so we can rebind the volume
    /// listener to the new endpoint). Other events are no-ops.
    /// </summary>
    private sealed class NotificationClient : IMMNotificationClient
    {
        private readonly HostVolumeService _svc;
        public NotificationClient(HostVolumeService svc) { _svc = svc; }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Render+Multimedia is the role the system-tray slider drives.
            // Other roles (Communications, Console) we ignore.
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            _svc._dispatcher.TryEnqueue(() => _svc.BindToCurrentDefault());
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_enumerator != null && _notificationClient != null)
                _enumerator.UnregisterEndpointNotificationCallback(_notificationClient);
        }
        catch { }
        UnbindCurrentDevice();
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
        _notificationClient = null;
    }
}
