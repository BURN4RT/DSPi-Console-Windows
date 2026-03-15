using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole;

public sealed partial class GraphWindow : Window
{
    private readonly MainViewModel _viewModel;

    public GraphWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BodePlot.DataContext = _viewModel;
        BodePlot.SetIsPopout(true);

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow?.Resize(new Windows.Graphics.SizeInt32(800, 500));
        appWindow!.Title = "Filter Response";

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

        _viewModel.VisibilityChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(UpdateLegend);
        };

        _viewModel.ActiveOutputsChanged += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(InitializeLegend);
        };

        RootGrid.Loaded += (_, _) => InitializeLegend();
    }

    public void SetSelectedChannel(int channelId) => BodePlot.SetSelectedChannel(channelId);

    private void InitializeLegend()
    {
        LegendPanel.Children.Clear();

        foreach (var channel in Channel.Inputs)
            AddLegendButton(channel);

        for (int o = 0; o < _viewModel.ActiveOutputs.Count; o++)
        {
            if (!_viewModel.IsOutputEnabled(o)) continue;
            AddLegendButton(_viewModel.ActiveOutputs[o]);
        }

        UpdateLegend();
    }

    private void AddLegendButton(Channel channel)
    {
        var btn = new Button
        {
            Tag = channel,
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        var indicator = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(channel.Color)
        };

        var label = new TextBlock
        {
            Text = channel.Descriptor,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        };

        panel.Children.Add(indicator);
        panel.Children.Add(label);
        btn.Content = panel;

        btn.Click += (s, _) =>
        {
            if (s is Button b && b.Tag is Channel ch)
                _viewModel.ToggleChannelVisibility(ch);
        };

        LegendPanel.Children.Add(btn);
    }

    private void UpdateLegend()
    {
        foreach (var child in LegendPanel.Children)
        {
            if (child is not Button btn || btn.Tag is not Channel channel) continue;

            bool isVisible = _viewModel.GetChannelVisibility(channel);
            if (btn.Content is StackPanel panel)
            {
                if (panel.Children[0] is Ellipse ellipse)
                {
                    ellipse.Fill = new SolidColorBrush(isVisible ? channel.Color : Colors.Gray);
                    ellipse.Opacity = isVisible ? 1.0 : 0.5;
                }

                if (panel.Children[1] is TextBlock text)
                    text.Opacity = isVisible ? 1.0 : 0.5;
            }

            btn.Background = new SolidColorBrush(
                isVisible ? Windows.UI.Color.FromArgb(38, channel.Color.R, channel.Color.G, channel.Color.B) : Colors.Transparent);
        }
    }
}
