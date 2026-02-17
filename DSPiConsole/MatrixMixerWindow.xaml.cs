using DSPiConsole.Core.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace DSPiConsole;

public sealed partial class MatrixMixerWindow : Window
{
    private readonly MainViewModel _viewModel;

    // Route UI controls: key = (inputIndex, outputIndex)
    private readonly Dictionary<(int, int), Border> _routeCircles = new();
    private readonly Dictionary<(int, int), TextBlock> _routeGainTexts = new();
    private readonly Dictionary<(int, int), Button> _routeInvButtons = new();
    private readonly Dictionary<(int, int), bool> _routeConnected = new();

    // Output controls: key = outputIndex
    private readonly Dictionary<int, Button> _outputEnableButtons = new();
    private readonly Dictionary<int, bool> _outputEnabled = new();
    private readonly Dictionary<int, TextBlock> _outputGainTexts = new();
    private readonly Dictionary<int, TextBlock> _outputDelayTexts = new();
    private readonly Dictionary<int, Button> _outputMuteButtons = new();

    // Shared cell border brush (reused across all cells)
    private static readonly SolidColorBrush CellBorderBrush =
        new(Windows.UI.Color.FromArgb(25, 255, 255, 255));

    public MatrixMixerWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow!.Title = "Matrix Mixer";

        if (appWindow.TitleBar is { } titleBar)
        {
            titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
            titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 22, 22, 22);
            titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 130, 130, 130);
            titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 22, 22, 22);
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 210, 210, 210);
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 22, 22, 22);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 120, 120, 120);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 22, 22, 22);
            titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 48, 48, 48);
        }

        BuildUI();

        // Size window to content after first layout: fonts are measured and DPI scale is known.
        // DesiredSize is in DIPs; AppWindow.Resize takes physical pixels.
        // Non-client height (titlebar + frame) is derived empirically — TitleBar.Height returns
        // 0 as an int (not null) so a null-coalescing fallback would silently miss it.
        bool sized = false;
        RootGrid.Loaded += (s, e) =>
        {
            if (sized) return;
            sized = true;
            double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            int nonClientH = appWindow.Size.Height - (int)Math.Round(RootGrid.ActualHeight * scale);
            RootGrid.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = RootGrid.DesiredSize;
            appWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Ceiling(desired.Width * scale),
                (int)Math.Ceiling(desired.Height * scale) + nonClientH));
        };
    }

    private void BuildUI()
    {
        var outputs = Channel.Outputs;
        int outputCount = outputs.Count;

        // ── Inner table grid ─────────────────────────────────────────
        var grid = new Grid();

        // Columns: label (col 0) + one per output
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        for (int o = 0; o < outputCount; o++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 95 });

        // Rows: 0=headers, 1=routing bar, 2=input L, 3=divider, 4=input R, 5=output bar,
        //       6=enable, 7=gain, 8=delay, 9=mute
        for (int r = 0; r < 10; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Row 0: Header background ──
        var headerBg = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20)),
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(headerBg, outputCount + 1);
        grid.Children.Add(headerBg);

        // ── Row 0: Output column headers ──
        for (int o = 0; o < outputCount; o++)
        {
            var ch = outputs[o];
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 6,
                Padding = new Thickness(0, 16, 0, 16)
            };

            panel.Children.Add(new TextBlock
            {
                Text = ch.Name,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Center
            });

            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, ch.Color.R, ch.Color.G, ch.Color.B)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(75, ch.Color.R, ch.Color.G, ch.Color.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ch.Descriptor,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(195, ch.Color.R, ch.Color.G, ch.Color.B)),
                    CharacterSpacing = 80
                }
            });

            Grid.SetColumn(panel, o + 1);
            grid.Children.Add(panel);
        }

        // ── Row 1: ROUTING section bar ──
        AddSectionBar(grid, 1, "ROUTING", outputCount);

        // ── Row 2: Input L ──
        var inputs = Channel.Inputs;
        AddInputRow(grid, 2, inputs[0], "Input L", outputCount);

        // ── Row 3: Input divider ──
        var inputDivider = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(inputDivider, outputCount + 1);
        Grid.SetRow(inputDivider, 3);
        grid.Children.Add(inputDivider);

        // ── Row 4: Input R ──
        AddInputRow(grid, 4, inputs[1], "Input R", outputCount);

        // ── Row 5: OUTPUT section bar ──
        AddSectionBar(grid, 5, "OUTPUT", outputCount);

        // ── Rows 6–9: Output data rows ──
        AddOutputDataRow(grid, 6, "ENABLE", outputCount, isLast: false,
            makeCell: o =>
            {
                _outputEnabled[o] = false;
                var btn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE7E8", FontSize = 15,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 200, 200, 220)) },
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = o
                };
                btn.Click += (s, e) =>
                {
                    bool nowEnabled = !_outputEnabled[o];
                    _outputEnabled[o] = nowEnabled;
                    if (btn.Content is FontIcon icon)
                        icon.Foreground = nowEnabled
                            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 74, 143, 227))
                            : new SolidColorBrush(Windows.UI.Color.FromArgb(120, 200, 200, 220));
                    btn.Background = nowEnabled
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 74, 143, 227))
                        : new SolidColorBrush(Colors.Transparent);
                };
                _outputEnableButtons[o] = btn;
                return btn;
            });

        AddOutputDataRow(grid, 7, "GAIN", outputCount, isLast: false,
            makeCell: o =>
            {
                var text = new TextBlock
                {
                    Text = "0 dB",
                    FontSize = 12,
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _outputGainTexts[o] = text;
                return text;
            });

        AddOutputDataRow(grid, 8, "DELAY", outputCount, isLast: false,
            makeCell: o =>
            {
                var text = new TextBlock
                {
                    Text = "0 ms",
                    FontSize = 12,
                    FontFamily = new FontFamily("Cascadia Code, Consolas"),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _outputDelayTexts[o] = text;
                return text;
            });

        AddOutputDataRow(grid, 9, "MUTE", outputCount, isLast: true,
            makeCell: o =>
            {
                var btn = new Button
                {
                    Content = new FontIcon { Glyph = "\uE74F", FontSize = 15,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(120, 200, 200, 220)) },
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = o
                };
                _outputMuteButtons[o] = btn;
                return btn;
            });

        // ── Card wrapper ─────────────────────────────────────────────
        var card = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 22, 22, 22)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(16),
            Child = grid
        };

        RootGrid.Children.Add(card);
    }

    // Adds an input row (routing section): colored label + route cells per output
    private void AddInputRow(Grid grid, int row, Channel inputCh, string labelText, int outputCount)
    {
        var label = new TextBlock
        {
            Text = labelText,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(inputCh.Color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        Grid.SetColumn(label, 0);
        Grid.SetRow(label, row);
        grid.Children.Add(label);

        int inputIndex = inputCh == Channel.Inputs[0] ? 0 : 1;
        for (int outp = 0; outp < outputCount; outp++)
        {
            var cell = BuildRouteCell(inputIndex, outp, inputCh.Color);
            Grid.SetColumn(cell, outp + 1);
            Grid.SetRow(cell, row);
            grid.Children.Add(cell);
        }
    }

    // Adds an output data row (bottom section) with label in col 0 and cell content per output
    private void AddOutputDataRow(Grid grid, int row, string labelText, int outputCount,
        bool isLast, Func<int, UIElement> makeCell)
    {
        var bottomBorder = isLast ? 0 : 1;

        // Label cell (col 0)
        var labelCell = new Border
        {
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, bottomBorder),
            Padding = new Thickness(14, 12, 0, 12)
        };
        labelCell.Child = new TextBlock
        {
            Text = labelText,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 255, 255)),
            CharacterSpacing = 80,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(labelCell, 0);
        Grid.SetRow(labelCell, row);
        grid.Children.Add(labelCell);

        // Content cells (cols 1+)
        for (int o = 0; o < outputCount; o++)
        {
            var contentCell = new Border
            {
                BorderBrush = CellBorderBrush,
                BorderThickness = new Thickness(0, 0, 0, bottomBorder),
                Padding = new Thickness(0, 12, 0, 12)
            };
            contentCell.Child = makeCell(o);
            Grid.SetColumn(contentCell, o + 1);
            Grid.SetRow(contentCell, row);
            grid.Children.Add(contentCell);
        }
    }

    private FrameworkElement BuildRouteCell(int input, int output, Windows.UI.Color inputColor)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(0, 18, 0, 18)
        };

        // Gain text (above circle)
        var gainText = new TextBlock
        {
            Text = "0 dB",
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _routeGainTexts[(input, output)] = gainText;
        panel.Children.Add(gainText);

        // Connection circle — clickable toggle
        _routeConnected[(input, output)] = false;
        var circle = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 160, 160, 170)),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
            Tag = (input, output, inputColor)
        };
        circle.Tapped += (s, e) =>
        {
            var key = (input, output);
            bool nowConnected = !_routeConnected[key];
            _routeConnected[key] = nowConnected;
            SetRouteConnected(input, output, nowConnected);
        };
        _routeCircles[(input, output)] = circle;
        panel.Children.Add(circle);

        // INV button (below circle)
        var invBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "INV",
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(130, 200, 200, 220))
            },
            Padding = new Thickness(8, 2, 8, 2),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Center,
            Tag = (input, output)
        };
        _routeInvButtons[(input, output)] = invBtn;
        panel.Children.Add(invBtn);

        return panel;
    }

    // Full-width section header bar spanning all columns
    private void AddSectionBar(Grid grid, int row, string text, int outputCount)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        content.Children.Add(new Border
        {
            Width = 2,
            Height = 10,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        });

        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)),
            CharacterSpacing = 150,
            VerticalAlignment = VerticalAlignment.Center
        });

        var bar = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 28, 28)),
            BorderBrush = CellBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(14, 8, 14, 8),
            Child = content
        };

        Grid.SetColumnSpan(bar, outputCount + 1);
        Grid.SetRow(bar, row);
        grid.Children.Add(bar);
    }

    /// <summary>
    /// Toggles a route circle between connected (solid fill) and disconnected (hollow ring).
    /// Ready for future ViewModel hookup.
    /// </summary>
    public void SetRouteConnected(int input, int output, bool connected)
    {
        if (!_routeCircles.TryGetValue((input, output), out var circle))
            return;

        if (circle.Tag is not (int _, int _, Windows.UI.Color inputColor))
            return;

        if (connected)
        {
            circle.Background = new SolidColorBrush(inputColor);
            circle.BorderThickness = new Thickness(0);
        }
        else
        {
            circle.Background = new SolidColorBrush(Colors.Transparent);
            circle.BorderThickness = new Thickness(2);
            circle.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 160, 160, 170));
        }
    }
}
