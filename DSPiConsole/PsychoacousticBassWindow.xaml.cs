using System;
using System.Globalization;
using System.Runtime.InteropServices;
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole;

/// <summary>
/// Psychoacoustic bass editor (firmware wire V23). Enable + per-output mask +
/// five parameters (cutoff / harmonics / drive / character / original), each
/// pushed live to the device through the ViewModel. Mirrors the Loudness window.
/// </summary>
public sealed partial class PsychoacousticBassWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly MainViewModel _viewModel;
    private bool _isUpdating = true;
    private MaskChipGrid? _outputMaskGrid;

    public PsychoacousticBassWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        appWindow?.Resize(new Windows.Graphics.SizeInt32((int)(420 * dpiScale), (int)(680 * dpiScale)));
        if (appWindow != null) appWindow.Title = "Psychoacoustic Bass";

        if (appWindow?.TitleBar is { } titleBar)
        {
            titleBar.ForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.BackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.InactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.InactiveBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 220, 220, 220);
            titleBar.ButtonBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 140, 140, 140);
            titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 50, 50, 50);
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        BuildStartingPoints();
        BuildUi();
    }

    private void BuildUi()
    {
        if (!_viewModel.PsybassSupported)
        {
            UnsupportedBar.IsOpen = true;
            BodyPanel.Visibility = Visibility.Collapsed;
            EnableToggle.IsEnabled = false;
            return;
        }

        UnsupportedBar.IsOpen = false;
        BodyPanel.Visibility = Visibility.Visible;

        _isUpdating = true;
        EnableToggle.IsOn = _viewModel.PsybassEnabled;
        SetPair(CutoffSlider, CutoffBox, _viewModel.PsybassCutoffHz, "F0");
        SetPair(HarmonicsSlider, HarmonicsBox, _viewModel.PsybassHarmonicsDb, "F1");
        SetPair(DriveSlider, DriveBox, _viewModel.PsybassDriveDb, "F1");
        SetPair(CharacterSlider, CharacterBox, _viewModel.PsybassCharacterPct, "F0");
        SetPair(OriginalSlider, OriginalBox, _viewModel.PsybassOriginalDb, "F0");
        _isUpdating = false;

        BuildOutputMask();
    }

    private static void SetPair(Slider slider, TextBox box, float value, string fmt)
    {
        slider.Value = value;
        box.Text = value.ToString(fmt, CultureInfo.InvariantCulture);
    }

    // ── Per-output mask ──────────────────────────────────────────────────────

    private void BuildOutputMask()
    {
        int count = _viewModel.NumOutputChannels;
        var outputs = _viewModel.ActiveOutputs;
        _outputMaskGrid = new MaskChipGrid(
            MaskChipGrid.AllBits(count),
            bit => bit < outputs.Count ? outputs[bit].Name : $"Output {bit + 1}",
            OnOutputMaskToggle,
            stretch: true,
            captionForBit: bit => bit < outputs.Count && outputs[bit].ShortName == "PDM" ? "S" : null);

        OutputMaskHost.Children.Clear();
        OutputMaskHost.Children.Add(_outputMaskGrid.Root);
        _outputMaskGrid.SetMask((uint)_viewModel.PsybassOutputMask);

        int all = count >= 16 ? 0xFFFF : (1 << count) - 1;
        int subBit = -1;
        for (int i = 0; i < outputs.Count; i++)
            if (outputs[i].ShortName == "PDM") { subBit = i; break; }
        int excludeSub = subBit >= 0 ? all & ~(1 << subBit) : all;

        var flyout = new MenuFlyout();
        void AddPreset(string text, int mask)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) => _viewModel.PsybassOutputMask = mask;
            flyout.Items.Add(mi);
        }
        AddPreset("All outputs", all);
        if (subBit >= 0) AddPreset("Exclude sub (recommended)", excludeSub);
        AddPreset("None", 0x0000);
        OutputMaskPresets.Flyout = flyout;
    }

    private void OnOutputMaskToggle(int index, bool on) =>
        _viewModel.SetPsybassOutputChannel(index, on);

    // ── Starting-point presets ───────────────────────────────────────────────

    private void BuildStartingPoints()
    {
        var flyout = new MenuFlyout();
        void Add(string name, float cutoff, float harmonics, float drive, float character, float original)
        {
            var mi = new MenuFlyoutItem { Text = name };
            mi.Click += (_, _) =>
            {
                _viewModel.PsybassCutoffHz = cutoff;
                _viewModel.PsybassHarmonicsDb = harmonics;
                _viewModel.PsybassDriveDb = drive;
                _viewModel.PsybassCharacterPct = character;
                _viewModel.PsybassOriginalDb = original;
            };
            flyout.Items.Add(mi);
        }
        Add("Bookshelf speakers", 60, 3, 6, 50, -3);
        Add("Small Bluetooth speaker", 120, 6, 9, 60, -12);
        Add("Laptop / tablet", 180, 6, 8, 55, -18);
        Add("Headphone bass feel", 40, 2, 4, 40, 0);
        PresetButton.Flyout = flyout;
    }

    // ── ViewModel sync ───────────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.PsybassSupported):
                    BuildUi();
                    return;
                case nameof(MainViewModel.PsybassEnabled):
                    _isUpdating = true; EnableToggle.IsOn = _viewModel.PsybassEnabled; _isUpdating = false;
                    break;
                case nameof(MainViewModel.PsybassCutoffHz):
                    _isUpdating = true; SetPair(CutoffSlider, CutoffBox, _viewModel.PsybassCutoffHz, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.PsybassHarmonicsDb):
                    _isUpdating = true; SetPair(HarmonicsSlider, HarmonicsBox, _viewModel.PsybassHarmonicsDb, "F1"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.PsybassDriveDb):
                    _isUpdating = true; SetPair(DriveSlider, DriveBox, _viewModel.PsybassDriveDb, "F1"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.PsybassCharacterPct):
                    _isUpdating = true; SetPair(CharacterSlider, CharacterBox, _viewModel.PsybassCharacterPct, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.PsybassOriginalDb):
                    _isUpdating = true; SetPair(OriginalSlider, OriginalBox, _viewModel.PsybassOriginalDb, "F0"); _isUpdating = false;
                    break;
                case nameof(MainViewModel.PsybassOutputMask):
                    _outputMaskGrid?.SetMask((uint)_viewModel.PsybassOutputMask);
                    break;
            }
        });
    }

    // ── Control handlers ─────────────────────────────────────────────────────

    private void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdating) return;
        _viewModel.PsybassEnabled = EnableToggle.IsOn;
    }

    private void OnCutoffChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, CutoffBox, "F0", v => _viewModel.PsybassCutoffHz = v);
    private void OnCutoffText(object sender, TextChangedEventArgs e) =>
        TextChanged(CutoffBox, CutoffSlider, PsybassLimits.CutoffMinHz, PsybassLimits.CutoffMaxHz, v => _viewModel.PsybassCutoffHz = v);

    private void OnHarmonicsChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, HarmonicsBox, "F1", v => _viewModel.PsybassHarmonicsDb = v);
    private void OnHarmonicsText(object sender, TextChangedEventArgs e) =>
        TextChanged(HarmonicsBox, HarmonicsSlider, PsybassLimits.HarmonicsMinDb, PsybassLimits.HarmonicsMaxDb, v => _viewModel.PsybassHarmonicsDb = v);

    private void OnDriveChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, DriveBox, "F1", v => _viewModel.PsybassDriveDb = v);
    private void OnDriveText(object sender, TextChangedEventArgs e) =>
        TextChanged(DriveBox, DriveSlider, PsybassLimits.DriveMinDb, PsybassLimits.DriveMaxDb, v => _viewModel.PsybassDriveDb = v);

    private void OnCharacterChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, CharacterBox, "F0", v => _viewModel.PsybassCharacterPct = v);
    private void OnCharacterText(object sender, TextChangedEventArgs e) =>
        TextChanged(CharacterBox, CharacterSlider, PsybassLimits.CharacterMinPct, PsybassLimits.CharacterMaxPct, v => _viewModel.PsybassCharacterPct = v);

    private void OnOriginalChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) =>
        SliderChanged((float)e.NewValue, OriginalBox, "F0", v => _viewModel.PsybassOriginalDb = v);
    private void OnOriginalText(object sender, TextChangedEventArgs e) =>
        TextChanged(OriginalBox, OriginalSlider, PsybassLimits.OriginalMinDb, PsybassLimits.OriginalMaxDb, v => _viewModel.PsybassOriginalDb = v);

    private void SliderChanged(float value, TextBox box, string fmt, Action<float> apply)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        apply(value);
        box.Text = value.ToString(fmt, CultureInfo.InvariantCulture);
        _isUpdating = false;
    }

    private void TextChanged(TextBox box, Slider slider, float min, float max, Action<float> apply)
    {
        if (_isUpdating) return;
        if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            _isUpdating = true;
            value = Math.Clamp(value, min, max);
            apply(value);
            slider.Value = value;
            _isUpdating = false;
        }
    }
}
