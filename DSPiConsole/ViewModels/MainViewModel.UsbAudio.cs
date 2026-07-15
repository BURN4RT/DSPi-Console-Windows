using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DSPiConsole.Services;

namespace DSPiConsole.ViewModels;

/// <summary>
/// USB input channel count sourced from Windows. The DSPi appears to Windows as a
/// playback (render) endpoint; the channel count of its selected format — the
/// alt-mode the user picks in Sound Settings — is how many channels the PC streams
/// into the DSPi over USB. We read it from the OS (Core Audio) rather than USB
/// alt-mode notifications so the displayed input count stays accurate to the user's
/// format selection even with no audio playing (matching the macOS app).
/// </summary>
public partial class MainViewModel
{
    /// <summary>Number of USB input channels the PC is configured to stream to the
    /// DSPi (from the Windows render endpoint's selected format). Falls back to the
    /// device-reported input count if the endpoint can't be read.</summary>
    [ObservableProperty]
    private int _usbInputChannelCount = 2;

    /// <summary>Re-read the USB input channel count from the Windows audio endpoint.
    /// Does the COM query off the UI thread; marshals the property update back.
    /// Blocking on the caller — invoke via Task.Run.</summary>
    public void RefreshUsbInputChannelCount()
    {
        int maxInputs = Math.Max(1, InputChannelCountForPlatform(Platform));
        int? fromWindows = WindowsAudioDevices.GetDspiRenderChannelCount();
        int count = fromWindows ?? _device.NumInputChannels;
        count = Math.Clamp(count, 1, maxInputs);

        if (count != UsbInputChannelCount)
            _dispatcher.TryEnqueue(() => UsbInputChannelCount = count);
    }
}
