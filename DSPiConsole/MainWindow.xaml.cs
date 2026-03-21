using System.Linq;
using DSPiConsole.Controls;
using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using DSPiConsole.Dialogs;
using DSPiConsole.Services;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using WinRT;

namespace DSPiConsole;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    public IReadOnlyList<Channel> InputChannels => Channel.Inputs;
    public IReadOnlyList<Channel> OutputChannels => Channel.Outputs;

    private Channel? _selectedChannel;
    private bool _isScrollAdjusting;
    private DateTime _lastFilterScrollTime = DateTime.MinValue;
    private bool _isUpdatingDelay;
    private bool _isUpdatingGain;
    private bool _closeConfirmed;
    private StatsWindow? _statsWindow;
    private GraphWindow? _graphWindow;
    private LoudnessWindow? _loudnessWindow;
    private CrossfeedWindow? _crossfeedWindow;
    private MatrixMixerWindow? _matrixMixerWindow;

    // Track output controls for live updates
    private TextBox? _currentGainTextBox;
    private TextBox? _currentDelayTextBox;
    private Slider? _currentGainSlider;
    private Slider? _currentDelaySlider;


    // Graph resize state
    private const double GraphMinHeight = 250;
    private const double GraphMaxHeight = 350;
    private bool _isResizingGraph;
    private double _graphResizeStartY;
    private double _graphResizeStartHeight;

    // Simple channel selection: 0 = dashboard, 1-5 = channel index
    private int _selectedChannelIndex = 0;
    private readonly List<ListViewItem> _channelListItems = new();
    private readonly Dictionary<int, TextBlock> _channelNameTexts = new();

    // Inline per-channel meters: keyed by ChannelId
    private readonly Dictionary<int, HorizontalMeterBar> _channelMeters = new();

    // Preset combo guard
    private bool _isUpdatingPresetCombo;

    // Dashboard rebuild debounce
    private DispatcherTimer? _dashboardDebounce;

    // Dashboard header stats TextBlocks: keyed by channelId
    private readonly Dictionary<int, TextBlock> _dashboardHeaderStats = new();

    // Pre-built output channel items: keyed by output index
    private readonly Dictionary<int, ListViewItem> _outputChannelItems = new();


    // Acrylic backdrop
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _configurationSource;

    public MainWindow()
    {
        InitializeComponent();
        this.ExtendsContentIntoTitleBar = true;

        SetupAcrylicBackdrop();
        this.SetTitleBar(AppTitleBar);

        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarDragRegion();
        TitleBarMenuButton.SizeChanged += (_, _) => UpdateTitleBarDragRegion();

        ViewModel = new MainViewModel();
        BodePlot.DataContext = ViewModel;
        BodePlot.SetDottedInactiveEnabled(AppSettings.Instance.DottedInactiveChannels);

        // Set window size
        var appWindow = GetAppWindow();
        if (appWindow != null)
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(1000, 825));
            appWindow.Title = "DSPi Console";
            appWindow.Closing += OnAppWindowClosing;
        }


        // Initialize channel lists
        InitializeChannelLists();

        // Initialize legend
        InitializeLegend();

        // Initialize dashboard
        InitializeDashboard();

        // Subscribe to ViewModel events
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FiltersChanged += (_, _) =>
        {
            BodePlot.Invalidate();
            ScheduleDashboardRefresh();
            if (_selectedChannel != null && !_isScrollAdjusting && !_isUpdatingGain && !_isUpdatingDelay)
                ShowChannelEditor(_selectedChannel);
        };
        AppSettings.Instance.SettingsChanged += (_, _) =>
        {
            if (_graphWindow == null) return;
            bool follows = AppSettings.Instance.PopoutFollowsSelectedChannel;
            _graphWindow.SetIgnoreVisibility(!follows);
            if (follows && _selectedChannel != null)
                _graphWindow.SetSelectedChannel((int)_selectedChannel.Id);
            else if (!follows)
                _graphWindow.SetSelectedChannel(-1);
        };
        ViewModel.VisibilityChanged += (_, _) =>
        {
            UpdateLegend();
            BodePlot.Invalidate();
        };

        ViewModel.ChannelNameChanged += channelId =>
        {
            if (_channelNameTexts.TryGetValue(channelId, out var tb))
                tb.Text = ViewModel.GetChannelName(Channel.FromId((ChannelId)channelId));
        };

        ViewModel.ActiveOutputsChanged += (s, e) =>
            DispatcherQueue.TryEnqueue(() => { InitializeChannelLists(); InitializeLegend(); });

        ViewModel.OutputEnabledChanged += (outputIndex, enabled) =>
            DispatcherQueue.TryEnqueue(() => { OnOutputEnabledChanged(outputIndex, enabled); InitializeLegend(); if (DashboardPanel.Visibility == Visibility.Visible) UpdateDashboardCards(); });

        ViewModel.MatrixOutputGainChanged += outputIndex =>
            DispatcherQueue.TryEnqueue(() => SyncGainFromViewModel(outputIndex));

        ViewModel.MatrixOutputDelayChanged += outputIndex =>
            DispatcherQueue.TryEnqueue(() => SyncDelayFromViewModel(outputIndex));

        ViewModel.PresetsChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(RefreshPresetComboBox);

        // Right-click context menu on preset combo
        PresetComboBox.RightTapped += OnPresetComboRightTapped;

        // Right-click preamp slider to reset to 0 dB
        PreampSlider.RightTapped += (s, e) => { e.Handled = true; ViewModel.PreampDb = 0; };

        // Initial UI state
        UpdateConnectionStatus();
        UpdatePreampDisplay();
        UpdateBypassButton();

        // Initialize AutoEQ (load database in background)
        _ = InitializeAutoEQAsync();
    }

    private async Task InitializeAutoEQAsync()
    {
        await AutoEQManager.Instance.LoadDatabaseAsync();
        DispatcherQueue.TryEnqueue(RefreshAutoEQFavoritesMenu);
    }

    private AppWindow? GetAppWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private void SetupAcrylicBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported())
            return;

        _configurationSource = new SystemBackdropConfiguration
        {
            IsInputActive = true  // Always keep translucency visible, even when unfocused
        };
        this.Closed += (s, e) =>
        {
            _acrylicController?.Dispose();
            _acrylicController = null;
            _configurationSource = null;
        };

        // Sidebar and titlebar translucency settings
        _acrylicController = new DesktopAcrylicController
        {
            TintColor = Windows.UI.Color.FromArgb(255, 32, 32, 32),
            TintOpacity = 0.5f,
            LuminosityOpacity = 0.8f
        };

        _acrylicController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _acrylicController.SetSystemBackdropConfiguration(_configurationSource);
    }

    private void InitializeChannelLists()
    {
        // Build channel list items programmatically
        // Index 0 = dashboard (no item), 1+ = channels
        _channelListItems.Clear();
        _outputChannelItems.Clear();
        _channelMeters.Clear();

        InputChannelsList.Items.Clear();
        OutputChannelsList.Items.Clear();

        if (!ViewModel.IsDeviceConnected) return;

        int index = 1;
        foreach (var channel in Channel.Inputs)
        {
            var item = CreateChannelListItem(channel, index++);
            _channelListItems.Add(item);
            InputChannelsList.Items.Add(item);
        }

        // Pre-build all output items and add enabled ones
        for (int o = 0; o < ViewModel.ActiveOutputs.Count; o++)
        {
            var channel = ViewModel.ActiveOutputs[o];
            var item = CreateChannelListItem(channel, index);
            _outputChannelItems[o] = item;
            if (ViewModel.IsOutputEnabled(o))
            {
                item.Tag = (channel, index++);
                _channelListItems.Add(item);
                OutputChannelsList.Items.Add(item);
            }
        }
    }

    private void OnOutputEnabledChanged(int outputIndex, bool enabled)
    {
        if (!_outputChannelItems.TryGetValue(outputIndex, out var item)) return;

        if (enabled)
        {
            if (OutputChannelsList.Items.Contains(item)) return;
            // Insert at the correct position to maintain output order
            int insertAt = 0;
            for (int o = 0; o < outputIndex; o++)
            {
                if (ViewModel.IsOutputEnabled(o) && OutputChannelsList.Items.Contains(_outputChannelItems[o]))
                    insertAt++;
            }
            OutputChannelsList.Items.Insert(insertAt, item);
        }
        else
        {
            OutputChannelsList.Items.Remove(item);
        }

        // Re-index the flat list for selection tracking
        int inputCount = Channel.Inputs.Count;
        if (_channelListItems.Count > inputCount)
            _channelListItems.RemoveRange(inputCount, _channelListItems.Count - inputCount);

        int index = inputCount + 1;
        for (int o = 0; o < ViewModel.ActiveOutputs.Count; o++)
        {
            if (!ViewModel.IsOutputEnabled(o) || !_outputChannelItems.TryGetValue(o, out var outItem)) continue;
            outItem.Tag = (ViewModel.ActiveOutputs[o], index++);
            _channelListItems.Add(outItem);
        }

        UpdateChannelListSelection();
    }

    private ListViewItem CreateChannelListItem(Channel channel, int index)
    {
        // Store both channel and index in Tag
        var item = new ListViewItem
        {
            Tag = (channel, index),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        item.Tapped += OnChannelItemTapped;

        var grid = new Grid { Height = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = ViewModel.GetChannelName(channel),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var nameBox = new TextBox
        {
            Text = ViewModel.GetChannelName(channel),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Style = (Style)RootGrid.Resources["ChannelNameTextBoxStyle"]
        };
        var nameContainer = new Grid();
        nameContainer.Children.Add(nameText);
        nameContainer.Children.Add(nameBox);
        _channelNameTexts[(int)channel.Id] = nameText;
        Grid.SetColumn(nameContainer, 0);
        grid.Children.Add(nameContainer);

        void CommitSidebarName()
        {
            if (nameBox.Visibility != Visibility.Visible) return;
            nameBox.Visibility = Visibility.Collapsed;
            nameText.Visibility = Visibility.Visible;
            var name = nameBox.Text.Trim();
            if (!string.IsNullOrEmpty(name)) ViewModel.SetChannelName(channel, name);
            FocusSink.Focus(FocusState.Programmatic);
        }

        var flyout = new MenuFlyout();

        var copyItem = new MenuFlyoutItem { Text = "Copy Parameters" };
        copyItem.Click += (s, e) => ViewModel.CopyChannelParams(channel);

        var pasteItem = new MenuFlyoutItem { Text = "Paste Parameters" };
        pasteItem.Click += (s, e) =>
        {
            ViewModel.PasteChannelParams(channel);
            if (_selectedChannel == channel)
                ShowChannelEditor(channel);
        };

        var renameItem = new MenuFlyoutItem { Text = "Rename" };
        renameItem.Click += (s, e) =>
        {
            nameText.Visibility = Visibility.Collapsed;
            nameBox.Text = ViewModel.GetChannelName(channel);
            nameBox.Visibility = Visibility.Visible;
            nameBox.Focus(FocusState.Programmatic);
            nameBox.SelectAll();
        };

        flyout.Opening += (s, e) =>
        {
            pasteItem.IsEnabled = ViewModel.HasChannelClipboard;
        };

        flyout.Items.Add(copyItem);
        flyout.Items.Add(pasteItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(renameItem);

        item.ContextFlyout = flyout;

        nameBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; CommitSidebarName(); }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                nameBox.Visibility = Visibility.Collapsed;
                nameText.Visibility = Visibility.Visible;
                FocusSink.Focus(FocusState.Programmatic);
            }
        };
        nameBox.LostFocus += (s, e) => CommitSidebarName();

        // Modern pill-shaped badge with glow indicator
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(15, channel.Color.R, channel.Color.G, channel.Color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, channel.Color.R, channel.Color.G, channel.Color.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 2, 7, 2),
            MinWidth = 46,
            VerticalAlignment = VerticalAlignment.Center
        };

        var badgeContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // Glowing indicator dot with layered effect
        var dotContainer = new Grid
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Outer glow
        var dotGlow = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(Color.FromArgb(100, channel.Color.R, channel.Color.G, channel.Color.B))
        };
        dotContainer.Children.Add(dotGlow);

        // Inner bright dot
        var dotCore = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(channel.Color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        //dotContainer.Children.Add(dotCore);

        //badgeContent.Children.Add(dotContainer);

        var badgeText = new TextBlock
        {
            Text = channel.Descriptor,
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(230, channel.Color.R, channel.Color.G, channel.Color.B)),
            VerticalAlignment = VerticalAlignment.Center,
            CharacterSpacing = 80
        };
        badgeContent.Children.Add(badgeText);

        badge.Child = badgeContent;
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        // Inline meter bar
        var meter = new HorizontalMeterBar
        {
            MeterColor = channel.Color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0)
        };
        Grid.SetColumn(meter, 1);
        grid.Children.Add(meter);
        _channelMeters[(int)channel.Id] = meter;

        item.Content = grid;
        return item;
    }

    private void InitializeLegend()
    {
        LegendPanel.Children.Clear();

        // Input channels are always shown
        foreach (var channel in Channel.Inputs)
            AddLegendButton(channel);

        // Only show enabled output channels
        for (int o = 0; o < ViewModel.ActiveOutputs.Count; o++)
        {
            if (!ViewModel.IsOutputEnabled(o)) continue;
            AddLegendButton(ViewModel.ActiveOutputs[o]);
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

        btn.Click += (s, e) =>
        {
            if (s is Button b && b.Tag is Channel ch)
            {
                ViewModel.ToggleChannelVisibility(ch);
            }
        };

        LegendPanel.Children.Add(btn);
    }

    private void UpdateLegend()
    {
        foreach (var child in LegendPanel.Children)
        {
            if (child is Button btn && btn.Tag is Channel channel)
            {
                bool isVisible = ViewModel.GetChannelVisibility(channel);
                var panel = btn.Content as StackPanel;
                if (panel != null)
                {
                    var ellipse = panel.Children[0] as Ellipse;
                    var text = panel.Children[1] as TextBlock;

                    if (ellipse != null)
                    {
                        ellipse.Fill = new SolidColorBrush(isVisible ? channel.Color : Colors.Gray);
                        ellipse.Opacity = isVisible ? 1.0 : 0.5;
                    }

                    if (text != null)
                    {
                        text.Opacity = isVisible ? 1.0 : 0.5;
                    }
                }

                btn.Background = new SolidColorBrush(
                    isVisible ? Color.FromArgb(38, channel.Color.R, channel.Color.G, channel.Color.B) : Colors.Transparent);
            }
        }
    }

    private void ScheduleDashboardRefresh()
    {
        if (DashboardPanel.Visibility != Visibility.Visible) return;
        _dashboardDebounce?.Stop();
        _dashboardDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _dashboardDebounce.Tick += (s, e) =>
        {
            _dashboardDebounce!.Stop();
            InitializeDashboard();
        };
        _dashboardDebounce.Start();
    }

    private void InitializeDashboard()
    {
        _dashboardHeaderStats.Clear();

        var savedTransitions = DashboardPanel.ChildrenTransitions;
        DashboardPanel.ChildrenTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();

        DashboardPanel.Children.Clear();

        if (!ViewModel.IsDeviceConnected)
        {
            DashboardPanel.ChildrenTransitions = savedTransitions;
            return;
        }

        foreach (var (key, card) in BuildDashboardCards())
        {
            card.Tag = key;
            DashboardPanel.Children.Add(card);
        }

        DashboardPanel.ChildrenTransitions = savedTransitions;
    }

    private void UpdateDashboardCards()
    {
        if (!ViewModel.IsDeviceConnected) return;

        _dashboardHeaderStats.Clear();
        var desired = BuildDashboardCards();
        var desiredKeys = desired.Select(d => d.key).ToList();

        // Remove cards that should no longer exist
        for (int i = DashboardPanel.Children.Count - 1; i >= 0; i--)
        {
            var key = ((FrameworkElement)DashboardPanel.Children[i]).Tag as string;
            if (key == null || !desiredKeys.Contains(key))
                DashboardPanel.Children.RemoveAt(i);
        }

        // Get current keys after removal
        var currentKeys = DashboardPanel.Children
            .Cast<FrameworkElement>()
            .Select(c => c.Tag as string)
            .ToList();

        // Add missing cards at correct positions
        for (int i = 0; i < desired.Count; i++)
        {
            var (key, card) = desired[i];
            if (!currentKeys.Contains(key))
            {
                card.Tag = key;
                DashboardPanel.Children.Insert(Math.Min(i, DashboardPanel.Children.Count), card);
                currentKeys.Insert(Math.Min(i, currentKeys.Count), key);
            }
        }
    }

    private List<(string key, FrameworkElement card)> BuildDashboardCards()
    {
        var cards = new List<(string key, FrameworkElement card)>();

        // Stereo Input Card (always shown when connected)
        cards.Add(("input", CreateStereoDashboardCard("STEREO INPUT (USB)", Channel.MasterLeft, Channel.MasterRight, false)));

        // Build output cards for enabled channels, pairing stereo L/R
        var outputs = ViewModel.ActiveOutputs;
        var processed = new HashSet<int>();

        for (int o = 0; o < outputs.Count; o++)
        {
            if (!ViewModel.IsOutputEnabled(o) || processed.Contains(o)) continue;

            var ch = outputs[o];

            // Check for stereo pair: consecutive L/R channels with adjacent IDs
            int pairIndex = -1;
            if (o + 1 < outputs.Count && (int)outputs[o + 1].Id == (int)ch.Id + 1 && ViewModel.IsOutputEnabled(o + 1))
                pairIndex = o + 1;

            if (pairIndex >= 0)
            {
                var left = ch;
                var right = outputs[pairIndex];
                cards.Add(($"{left.ShortName}-{right.ShortName}", CreateStereoDashboardCard($"{left.Name} / {right.Name}", left, right, true)));
                processed.Add(o);
                processed.Add(pairIndex);
            }
            else
            {
                cards.Add((ch.ShortName, CreateMonoDashboardCard(ch)));
                processed.Add(o);
            }
        }

        return cards;
    }

    private Border CreateStereoDashboardCard(string title, Channel left, Channel right, bool showDelay)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(153, 45, 45, 48)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
            BorderThickness = new Thickness(1)
        };

        var mainStack = new StackPanel();

        // Header row
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        headerGrid.Children.Add(CreateChannelHeader(left, showDelay, 0));
        headerGrid.Children.Add(CreateChannelHeader(right, showDelay, 1));

        mainStack.Children.Add(headerGrid);
        mainStack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)) });

        // Filter rows
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftFilters = CreateDashboardFilterList(left);
        Grid.SetColumn(leftFilters, 0);
        contentGrid.Children.Add(leftFilters);

        var divider = new Border { Background = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)) };
        Grid.SetColumn(divider, 1);
        contentGrid.Children.Add(divider);

        var rightFilters = CreateDashboardFilterList(right);
        Grid.SetColumn(rightFilters, 2);
        contentGrid.Children.Add(rightFilters);

        mainStack.Children.Add(contentGrid);
        card.Child = mainStack;

        return card;
    }

    private Border CreateChannelHeader(Channel channel, bool showDelay, int column)
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(25, channel.Color.R, channel.Color.G, channel.Color.B)),
            Padding = new Thickness(8)
        };
        Grid.SetColumn(header, column);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        panel.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new SolidColorBrush(channel.Color)
        });

        panel.Children.Add(new TextBlock
        {
            Text = channel.Name,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(channel.Color)
        });

        if (showDelay)
        {
            var gain = ViewModel.GetChannelGain(channel);
            var isMuted = ViewModel.GetChannelMute(channel);

            var statsText = new TextBlock
            {
                Text = $"{gain:F1}dB  {ViewModel.GetChannelDelay(channel):F0}ms{(isMuted ? "  MUTED" : "")}",
                FontSize = 9,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Foreground = new SolidColorBrush(isMuted ? Color.FromArgb(255, 200, 80, 80) : Colors.Gray),
                Margin = new Thickness(8, 0, 0, 0)
            };
            _dashboardHeaderStats[(int)channel.Id] = statsText;
            panel.Children.Add(statsText);
        }

        header.Child = panel;
        return header;
    }

    private StackPanel CreateDashboardFilterList(Channel channel)
    {
        var stack = new StackPanel();
        var filters = ViewModel.GetFilters(channel);

        for (int i = 0; i < filters.Count; i++)
        {
            var row = CreateDashboardFilterRow(i + 1, filters[i], channel.Color);
            row.Background = new SolidColorBrush(i % 2 == 0 ? Color.FromArgb(8, 255, 255, 255) : Colors.Transparent);
            stack.Children.Add(row);
        }

        return stack;
    }

    private Grid CreateDashboardFilterRow(int band, FilterParams p, Color color)
    {
        var grid = new Grid { Height = 24, Padding = new Thickness(8, 0, 8, 0), ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });  // 0: Band #
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });  // 1: Type
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2: Spacer
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });  // 3: Freq
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });  // 4: Gain
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });  // 5: Q

        bool isActive = p.Type != FilterType.Flat;

        var bandText = new TextBlock
        {
            Text = band.ToString(),
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Code"),
            Foreground = new SolidColorBrush(Color.FromArgb(178, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(bandText, 0);
        grid.Children.Add(bandText);

        var typeText = new TextBlock
        {
            Text = p.Type.GetShortName(),
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(isActive ? color : Color.FromArgb(102, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(typeText, 1);
        grid.Children.Add(typeText);

        if (isActive)
        {
            var secondaryBrush = (SolidColorBrush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            var tertiaryBrush = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"];

            var freqPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
            freqPanel.Children.Add(new TextBlock
            {
                Text = $"{p.Frequency:F0}",
                FontSize = 10,
                FontFamily = new FontFamily("Cascadia Code"),
                Foreground = secondaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            freqPanel.Children.Add(new TextBlock
            {
                Text = "Hz",
                FontSize = 8,
                Foreground = tertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(freqPanel, 3);
            grid.Children.Add(freqPanel);

            if (p.Type.HasGain())
            {
                var gainPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
                gainPanel.Children.Add(new TextBlock
                {
                    Text = FormatFilterValueSigned(p.Gain),
                    FontSize = 10,
                    FontFamily = new FontFamily("Cascadia Code"),
                    Foreground = secondaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                gainPanel.Children.Add(new TextBlock
                {
                    Text = "dB",
                    FontSize = 8,
                    Foreground = tertiaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(gainPanel, 4);
                grid.Children.Add(gainPanel);
            }

            if (p.Type.HasQ())
            {
                var qPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right };
                qPanel.Children.Add(new TextBlock
                {
                    Text = FormatFilterValue(p.Q, 3),
                    FontSize = 10,
                    FontFamily = new FontFamily("Cascadia Code"),
                    Foreground = secondaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                qPanel.Children.Add(new TextBlock
                {
                    Text = "Q",
                    FontSize = 8,
                    Foreground = tertiaryBrush,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(qPanel, 5);
                grid.Children.Add(qPanel);
            }
        }
        else
        {
            var dash = new TextBlock
            {
                Text = "—",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(51, 128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(dash, 3);
            grid.Children.Add(dash);
        }

        return grid;
    }

    private Border CreateMonoDashboardCard(Channel channel)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(153, 45, 45, 48)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(76, channel.Color.R, channel.Color.G, channel.Color.B)),
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel();
        stack.Children.Add(CreateChannelHeader(channel, true, 0));
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(51, channel.Color.R, channel.Color.G, channel.Color.B)) });
        stack.Children.Add(CreateDashboardFilterList(channel));

        card.Child = stack;
        return card;
    }

    private void ShowChannelEditor(Channel channel)
    {
        _selectedChannel = channel;
        BodePlot.SetSelectedChannel((int)channel.Id);
        if (AppSettings.Instance.PopoutFollowsSelectedChannel)
            _graphWindow?.SetSelectedChannel((int)channel.Id);
        DashboardPanel.Visibility = Visibility.Collapsed;
        ChannelEditorPanel.Visibility = Visibility.Visible;

        ChannelEditorPanel.Children.Clear();

        if (channel.Id == ChannelId.MasterLeft || channel.Id == ChannelId.MasterRight)
        {
            var clearBtn = new Button { Content = "Clear All Master PEQ", HorizontalAlignment = HorizontalAlignment.Right };
            clearBtn.Click += (s, e) => ViewModel.ClearAllMasterCommand.Execute(null);
            ChannelEditorPanel.Children.Add(clearBtn);
        }

        // Output channel controls: Gain, Delay, Mute
        if (channel.IsOutput)
        {
            bool isMuted = ViewModel.GetChannelMute(channel);
            var dimBrush = new SolidColorBrush(Color.FromArgb(160, 180, 180, 180));
            var unitBrush = new SolidColorBrush(Color.FromArgb(140, 180, 180, 180));

            var outputCard = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(128, 45, 45, 48)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 16),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var cardGrid = new Grid { ColumnSpacing = 16 };
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ── Gain section (col 0) ──
            var gainSection = new StackPanel { Spacing = 6 };

            var gainLabelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            gainLabelPanel.Children.Add(new FontIcon { Glyph = "\uE767", FontSize = 12, Foreground = dimBrush });
            gainLabelPanel.Children.Add(new TextBlock
            {
                Text = "GAIN", FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = dimBrush
            });
            gainSection.Children.Add(gainLabelPanel);

            var gainSliderRow = new Grid();
            gainSliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gainSliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var gainSlider = new Slider
            {
                Minimum = -60, Maximum = 10,
                Value = ViewModel.GetChannelGain(channel),
                Tag = channel, StepFrequency = 0.5
            };
            gainSlider.ValueChanged += OnGainSliderChanged;
            gainSlider.RightTapped += (s, e) =>
            {
                e.Handled = true;
                if (s is Slider sl && sl.Tag is Channel ch)
                { ViewModel.SetChannelGain((int)ch.Id, 0); sl.Value = 0; }
            };
            Grid.SetColumn(gainSlider, 0);
            gainSliderRow.Children.Add(gainSlider);
            _currentGainSlider = gainSlider;

            var gainValuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var gainTextBox = new TextBox
            {
                Tag = channel, Width = 44,
                Text = ViewModel.GetChannelGain(channel).ToString("F1"),
                FontSize = 13,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"]
            };
            gainTextBox.TextChanged += OnGainTextChanged;
            gainTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    // Move focus to hidden sink to clear selection and cursor
                    FocusSink.Focus(FocusState.Programmatic);
                }
            };
            gainValuePanel.Children.Add(gainTextBox);
            gainValuePanel.Children.Add(new TextBlock { Text = "dB", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = unitBrush });
            gainValuePanel.PointerWheelChanged += (s, ev) =>
            {
                var delta = ev.GetCurrentPoint(gainValuePanel).Properties.MouseWheelDelta;
                if (delta == 0) return;
                int direction = delta > 0 ? 1 : -1;
                float current = ViewModel.GetChannelGain(channel);
                float newVal = Math.Clamp(current + direction * 0.1f, -60, 10);
                _isUpdatingGain = true;
                ViewModel.SetChannelGain((int)channel.Id, newVal);
                gainTextBox.Text = newVal.ToString("F1");
                gainSlider.Value = newVal;
                _isUpdatingGain = false;
                ev.Handled = true;
            };
            Grid.SetColumn(gainValuePanel, 1);
            gainSliderRow.Children.Add(gainValuePanel);
            _currentGainTextBox = gainTextBox;

            gainSection.Children.Add(gainSliderRow);
            Grid.SetColumn(gainSection, 0);
            cardGrid.Children.Add(gainSection);

            // Vertical separator (col 1)
            var separator = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                Width = 1, VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(separator, 1);
            cardGrid.Children.Add(separator);

            // ── Delay section (col 2) ──
            var delaySection = new StackPanel { Spacing = 6 };

            var delayLabelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            delayLabelPanel.Children.Add(new FontIcon { Glyph = "\uED5A", FontSize = 12, Foreground = dimBrush });
            delayLabelPanel.Children.Add(new TextBlock
            {
                Text = "DELAY", FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = dimBrush
            });
            delaySection.Children.Add(delayLabelPanel);

            var delaySliderRow = new Grid();
            delaySliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            delaySliderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var delaySlider = new Slider
            {
                Minimum = 0, Maximum = 170,
                Value = ViewModel.GetChannelDelay(channel),
                Tag = channel, StepFrequency = 1
            };
            delaySlider.ValueChanged += OnDelaySliderChanged;
            delaySlider.RightTapped += (s, e) =>
            {
                e.Handled = true;
                if (s is Slider sl && sl.Tag is Channel ch)
                { ViewModel.SetDelay((int)ch.Id, 0); sl.Value = 0; }
            };
            Grid.SetColumn(delaySlider, 0);
            delaySliderRow.Children.Add(delaySlider);
            _currentDelaySlider = delaySlider;

            var delayValuePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var delayTextBox = new TextBox
            {
                Tag = channel, Width = 34,
                Text = ViewModel.GetChannelDelay(channel).ToString("F0"),
                FontSize = 13,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"]
            };
            delayTextBox.TextChanged += OnDelayTextChanged;
            delayTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    // Move focus to hidden sink to clear selection and cursor
                    FocusSink.Focus(FocusState.Programmatic);
                }
            };
            delayValuePanel.Children.Add(delayTextBox);
            delayValuePanel.Children.Add(new TextBlock { Text = "ms", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = unitBrush });
            delayValuePanel.PointerWheelChanged += (s, ev) =>
            {
                var delta = ev.GetCurrentPoint(delayValuePanel).Properties.MouseWheelDelta;
                if (delta == 0) return;
                int direction = delta > 0 ? 1 : -1;
                float current = ViewModel.GetChannelDelay(channel);
                float newVal = Math.Clamp(current + direction, 0, 170);
                _isUpdatingDelay = true;
                ViewModel.SetDelay((int)channel.Id, newVal);
                delayTextBox.Text = newVal.ToString("F0");
                delaySlider.Value = newVal;
                _isUpdatingDelay = false;
                ev.Handled = true;
            };
            Grid.SetColumn(delayValuePanel, 1);
            delaySliderRow.Children.Add(delayValuePanel);
            _currentDelayTextBox = delayTextBox;

            delaySection.Children.Add(delaySliderRow);
            Grid.SetColumn(delaySection, 2);
            cardGrid.Children.Add(delaySection);

            // Vertical separator (col 3)
            var muteSeparator = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                Width = 1, VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(muteSeparator, 3);
            cardGrid.Children.Add(muteSeparator);

            // ── Mute icon (col 4) ──
            var muteBtn = new ToggleButton
            {
                Tag = channel, IsChecked = isMuted,
                Padding = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            muteBtn.Content = new FontIcon
            {
                Glyph = isMuted ? "\uE74F" : "\uE767",
                FontSize = 16,
                Foreground = isMuted
                    ? new SolidColorBrush(Color.FromArgb(255, 80, 80, 80))
                    : new SolidColorBrush(Color.FromArgb(200, 200, 200, 200))
            };
            muteBtn.Click += OnMuteToggleClick;
            Grid.SetColumn(muteBtn, 4);
            cardGrid.Children.Add(muteBtn);

            outputCard.Child = cardGrid;
            ChannelEditorPanel.Children.Add(outputCard);
        }

        // Filter rows
        var filters = ViewModel.GetFilters(channel);
        for (int i = 0; i < filters.Count; i++)
        {
            ChannelEditorPanel.Children.Add(CreateFilterEditorRow(channel, i, filters[i]));
        }
    }

    private Border CreateFilterEditorRow(Channel channel, int bandIndex, FilterParams p)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 45, 45, 48)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) }); // Freq
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) }); // Q
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) }); // Gain
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnSpacing = 16;

        // Band label
        var bandLabel = new TextBlock
        {
            Text = $"Band {bandIndex + 1}",
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Code"),
            Foreground = new SolidColorBrush(Colors.Gray),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(bandLabel, 0);
        grid.Children.Add(bandLabel);

        // Filter type selector
        var typeCombo = new ComboBox { Width = 120, Tag = (channel, bandIndex) };
        foreach (var type in Enum.GetValues<FilterType>())
        {
            typeCombo.Items.Add(new ComboBoxItem { Content = type.GetDisplayName(), Tag = type });
        }
        typeCombo.SelectedIndex = (int)p.Type;
        typeCombo.SelectionChanged += OnFilterTypeChanged;
        Grid.SetColumn(typeCombo, 1);
        grid.Children.Add(typeCombo);

        // Frequency
        if (p.Type != FilterType.Flat)
        {
            var freqPanel = CreateValueField("Hz", p.Frequency, 58, (channel, bandIndex, "freq"));
            Grid.SetColumn(freqPanel, 2);
            grid.Children.Add(freqPanel);
        }

        // Q
        if (p.Type.HasQ())
        {
            var qPanel = CreateValueField("Q", p.Q, 44, (channel, bandIndex, "q"), decimals: 3);
            Grid.SetColumn(qPanel, 3);
            grid.Children.Add(qPanel);
        }

        // Gain (for peaking, low shelf, high shelf)
        if (p.Type.HasGain())
        {
            var gainPanel = CreateValueField("dB", p.Gain, 40, (channel, bandIndex, "gain"));
            Grid.SetColumn(gainPanel, 4);
            grid.Children.Add(gainPanel);
        }

        row.Child = grid;
        return row;
    }

    private static string FormatFilterValue(float value, int decimals = 2) =>
        decimals > 0 ? value.ToString($"F{decimals}").TrimEnd('0').TrimEnd('.') : value.ToString("F0");

    private static string FormatFilterValueSigned(float value) =>
        (value >= 0 ? "+" : "") + FormatFilterValue(value);

    private StackPanel CreateValueField(string label, float value, double width, (Channel channel, int band, string param) tag, int decimals = 2)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        var textBox = new TextBox
        {
            Width = width,
            Text = FormatFilterValue(value, decimals),
            Tag = tag,
            FontSize = 13,
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Style = (Style)RootGrid.Resources["InlineValueTextBoxStyle"]
        };
        textBox.LostFocus += OnFilterValueChanged;
        textBox.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                OnFilterValueChanged(s, null!);
                // Move focus to hidden sink to clear selection and cursor
                FocusSink.Focus(FocusState.Programmatic);
            }
        };

        panel.Children.Add(textBox);
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        });

        panel.PointerWheelChanged += (s, e) =>
        {
            var delta = e.GetCurrentPoint(panel).Properties.MouseWheelDelta;
            if (delta == 0) return;

            var now = DateTime.UtcNow;
            bool fast = (now - _lastFilterScrollTime).TotalMilliseconds < 40;
            _lastFilterScrollTime = now;

            int direction = delta > 0 ? 1 : -1;

            var filters = ViewModel.GetFilters(tag.channel);
            if (tag.band >= filters.Count) return;
            var p = filters[tag.band].Clone();

            switch (tag.param)
            {
                case "freq":
                    p.Frequency = Math.Clamp(p.Frequency + direction * (fast ? 10 : 1), 20, 20000);
                    break;
                case "q":
                    p.Q = Math.Clamp(p.Q + direction * (fast ? 0.1f : 0.01f), 0.1f, 20);
                    break;
                case "gain":
                    p.Gain = Math.Clamp(p.Gain + direction * (fast ? 0.1f : 0.01f), -20, 20);
                    break;
            }

            _isScrollAdjusting = true;
            ViewModel.SetFilterDeferred((int)tag.channel.Id, tag.band, p);
            _isScrollAdjusting = false;
            textBox.Text = FormatFilterValue(
                tag.param == "freq" ? p.Frequency : tag.param == "q" ? p.Q : p.Gain,
                tag.param == "q" ? 3 : tag.param == "freq" ? 0 : 2);
            e.Handled = true;
        };

        return panel;
    }

    private void ShowDashboard()
    {
        _selectedChannel = null;
        BodePlot.SetSelectedChannel(-1);
        if (AppSettings.Instance.PopoutFollowsSelectedChannel)
            _graphWindow?.SetSelectedChannel(-1);
        ChannelEditorPanel.Visibility = Visibility.Collapsed;
        DashboardPanel.Visibility = Visibility.Visible;
        InitializeDashboard(); // Refresh
    }

    #region Event Handlers

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsDeviceConnected):
                    UpdateConnectionStatus();
                    break;
                case nameof(MainViewModel.ErrorMessage):
                    UpdateConnectionStatus();
                    break;
                case nameof(MainViewModel.PreampDb):
                    UpdatePreampDisplay();
                    break;
                case nameof(MainViewModel.Bypass):
                    UpdateBypassButton();
                    break;
                case nameof(MainViewModel.Status):
                    UpdateMeters();
                    break;
            }
        });
    }

    private void UpdateConnectionStatus()
    {
        ConnectionIndicator.Fill = new SolidColorBrush(ViewModel.IsDeviceConnected ? Colors.LimeGreen : Colors.Red);
        ConnectionStatusText.Text = ViewModel.IsDeviceConnected ? "Connected" : (ViewModel.ErrorMessage ?? "Disconnected");

        if (!ViewModel.IsDeviceConnected)
        {
            InputChannelsList.Items.Clear();
            OutputChannelsList.Items.Clear();
            _channelListItems.Clear();
            _outputChannelItems.Clear();
            _channelMeters.Clear();

            FadeCurves(0);
            FadeElement(LegendPanel, 0);

            // Hide preset section
            PresetSection.Visibility = Visibility.Collapsed;

            // Return to empty dashboard view
            _selectedChannel = null;
            ChannelEditorPanel.Visibility = Visibility.Collapsed;
            ChannelEditorPanel.Children.Clear();
            DashboardPanel.Visibility = Visibility.Visible;
            DashboardPanel.Children.Clear();
        }
        else
        {
            InitializeChannelLists();
            InitializeLegend();

            FadeCurves(1);
            FadeElement(LegendPanel, 1);
        }
    }

    private DispatcherTimer? _curveFadeTimer;
    private double _curveFadeTarget;

    private void FadeCurves(double targetOpacity)
    {
        _curveFadeTarget = targetOpacity;
        _curveFadeTimer?.Stop();
        _curveFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _curveFadeTimer.Tick += (s, e) =>
        {
            double current = BodePlot.GetCurveOpacity();
            double diff = _curveFadeTarget - current;
            if (Math.Abs(diff) < 0.02)
            {
                BodePlot.SetCurveOpacity(_curveFadeTarget);
                _curveFadeTimer.Stop();
            }
            else
            {
                BodePlot.SetCurveOpacity(current + diff * 0.15);
            }
        };
        _curveFadeTimer.Start();
    }

    private void FadeElement(UIElement element, double targetOpacity)
    {
        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = targetOpacity == 0 ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        var sb = new Storyboard();
        sb.Children.Add(animation);
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        sb.Begin();
    }

    private void UpdatePreampDisplay()
    {
        if (!_isUpdatingDelay)
        {
            PreampSlider.Value = ViewModel.PreampDb;
        }
        PreampValueText.Text = $"{ViewModel.PreampDb:F1} dB";
    }

    private void UpdateBypassButton()
    {
        BypassButton.IsChecked = ViewModel.Bypass;
    }

    private void UpdateMeters()
    {
        var status = ViewModel.Status;

        // Update inline per-channel meters
        foreach (var (channelId, meter) in _channelMeters)
        {
            if (channelId < status.Peaks.Length)
                meter.Level = status.Peaks[channelId];
            meter.IsClipping = status.IsClipping((ChannelId)channelId);
            var channel = Channel.FromIndex(channelId);
            meter.IsMuted = channel.IsOutput && ViewModel.GetChannelMute(channel);
        }

        // Workaround: firmware reports 100% for Core 1 when idle/no audio
        // Treat 0%/100% as uninitialized and show 0% for both
        if (status.Cpu0Load == 0 && status.Cpu1Load == 100)
        {
            Cpu0Meter.Load = 0;
            Cpu1Meter.Load = 0;
        }
        else
        {
            Cpu0Meter.Load = status.Cpu0Load;
            Cpu1Meter.Load = status.Cpu1Load;
        }
    }

    private void OnChannelItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not ListViewItem item || item.Tag is not (Channel channel, int index))
            return;

        if (_selectedChannelIndex == index)
        {
            // Same channel clicked - go back to dashboard
            _selectedChannelIndex = 0;
            UpdateChannelListSelection();
            ViewModel.UpdateChannelSelection(null);
            ShowDashboard();
        }
        else
        {
            // Different channel clicked - select it
            _selectedChannelIndex = index;
            UpdateChannelListSelection();
            ViewModel.UpdateChannelSelection(channel);
            ShowChannelEditor(channel);
        }
    }

    private void UpdateChannelListSelection()
    {
        // Clear all selections first
        InputChannelsList.SelectedItem = null;
        OutputChannelsList.SelectedItem = null;

        // If a channel is selected (index > 0), highlight it
        if (_selectedChannelIndex > 0 && _selectedChannelIndex <= _channelListItems.Count)
        {
            var item = _channelListItems[_selectedChannelIndex - 1];
            if (InputChannelsList.Items.Contains(item))
                InputChannelsList.SelectedItem = item;
            else if (OutputChannelsList.Items.Contains(item))
                OutputChannelsList.SelectedItem = item;
        }
    }

    private void OnPreampSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Math.Abs(ViewModel.PreampDb - (float)e.NewValue) > 0.1f)
        {
            ViewModel.PreampDb = (float)e.NewValue;
        }
    }

    private void OnBypassToggled(object sender, RoutedEventArgs e)
    {
        ViewModel.Bypass = BypassButton.IsChecked == true;
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ReconnectCommand.Execute(null);
    }

    private void OnClearAllMasterClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAllMasterCommand.Execute(null);
    }

    private void OnDelaySliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingDelay) return;
        if (sender is Slider slider && slider.Tag is Channel channel)
        {
            _isUpdatingDelay = true;
            ViewModel.SetDelay((int)channel.Id, (float)e.NewValue);
            if (_currentDelayTextBox != null)
            {
                _currentDelayTextBox.Text = e.NewValue.ToString("F0");
            }
            _isUpdatingDelay = false;
        }
    }

    private void OnDelayTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingDelay) return;
        if (sender is TextBox textBox && textBox.Tag is Channel channel)
        {
            if (float.TryParse(textBox.Text, out float value))
            {
                _isUpdatingDelay = true;
                value = Math.Clamp(value, 0, 170);
                ViewModel.SetDelay((int)channel.Id, value);
                if (_currentDelaySlider != null)
                {
                    _currentDelaySlider.Value = value;
                }
                _isUpdatingDelay = false;
            }
        }
    }

    private void OnGainSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingGain) return;
        if (sender is Slider slider && slider.Tag is Channel channel)
        {
            _isUpdatingGain = true;
            ViewModel.SetChannelGain((int)channel.Id, (float)e.NewValue);
            if (_currentGainTextBox != null)
            {
                _currentGainTextBox.Text = e.NewValue.ToString("F1");
            }
            _isUpdatingGain = false;
        }
    }

    private void OnGainTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingGain) return;
        if (sender is TextBox textBox && textBox.Tag is Channel channel)
        {
            if (float.TryParse(textBox.Text, out float value))
            {
                _isUpdatingGain = true;
                value = Math.Clamp(value, -60, 10);
                ViewModel.SetChannelGain((int)channel.Id, value);
                if (_currentGainSlider != null)
                {
                    _currentGainSlider.Value = value;
                }
                _isUpdatingGain = false;
            }
        }
    }

    private void SyncGainFromViewModel(int outputIndex)
    {
        var outputs = ViewModel.ActiveOutputs;
        if (outputIndex < 0 || outputIndex >= outputs.Count) return;
        var channel = outputs[outputIndex];

        RefreshDashboardHeaderStats(channel);

        if (_selectedChannel != null && _selectedChannel.Id == channel.Id)
        {
            float gain = ViewModel.GetChannelGain(_selectedChannel);
            _isUpdatingGain = true;
            if (_currentGainSlider != null)
                _currentGainSlider.Value = gain;
            if (_currentGainTextBox != null)
                _currentGainTextBox.Text = gain.ToString("F1");
            _isUpdatingGain = false;
        }
    }

    private void SyncDelayFromViewModel(int outputIndex)
    {
        var outputs = ViewModel.ActiveOutputs;
        if (outputIndex < 0 || outputIndex >= outputs.Count) return;
        var channel = outputs[outputIndex];

        RefreshDashboardHeaderStats(channel);

        if (_selectedChannel != null && _selectedChannel.Id == channel.Id)
        {
            float delay = ViewModel.GetChannelDelay(_selectedChannel);
            _isUpdatingDelay = true;
            if (_currentDelaySlider != null)
                _currentDelaySlider.Value = delay;
            if (_currentDelayTextBox != null)
                _currentDelayTextBox.Text = delay.ToString("F0");
            _isUpdatingDelay = false;
        }
    }

    private void RefreshDashboardHeaderStats(Channel channel)
    {
        if (!_dashboardHeaderStats.TryGetValue((int)channel.Id, out var tb)) return;
        float gain = ViewModel.GetChannelGain(channel);
        float delay = ViewModel.GetChannelDelay(channel);
        bool muted = ViewModel.GetChannelMute(channel);
        tb.Text = $"{gain:F1}dB  {delay:F0}ms{(muted ? "  MUTED" : "")}";
        tb.Foreground = new SolidColorBrush(muted ? Color.FromArgb(255, 200, 80, 80) : Colors.Gray);
    }

    private void OnMuteToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Tag is Channel channel)
        {
            bool muted = btn.IsChecked == true;
            ViewModel.SetChannelMute((int)channel.Id, muted);

            // Update icon appearance
            if (btn.Content is FontIcon icon)
            {
                icon.Glyph = muted ? "\uE74F" : "\uE767";
                icon.Foreground = muted
                    ? new SolidColorBrush(Color.FromArgb(255, 80, 80, 80))
                    : new SolidColorBrush(Color.FromArgb(200, 200, 200, 200));
            }

            RefreshDashboardHeaderStats(channel);
        }
    }

    private void OnFilterTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.Tag is (Channel channel, int bandIndex))
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is FilterType newType)
            {
                var filters = ViewModel.GetFilters(channel);
                if (bandIndex < filters.Count)
                {
                    var p = filters[bandIndex].Clone();
                    p.Type = newType;
                    _ = ViewModel.SetFilter((int)channel.Id, bandIndex, p);

                    // Refresh the row
                    if (_selectedChannel != null)
                    {
                        ShowChannelEditor(_selectedChannel);
                    }
                }
            }
        }
    }

    private void OnFilterValueChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Tag is (Channel channel, int bandIndex, string param))
        {
            if (float.TryParse(textBox.Text, out float value))
            {
                var filters = ViewModel.GetFilters(channel);
                if (bandIndex < filters.Count)
                {
                    var p = filters[bandIndex].Clone();

                    switch (param)
                    {
                        case "freq":
                            p.Frequency = Math.Clamp(value, 20, 20000);
                            break;
                        case "q":
                            p.Q = Math.Clamp(value, 0.1f, 20);
                            break;
                        case "gain":
                            p.Gain = Math.Clamp(value, -20, 20);
                            break;
                    }

                    _ = ViewModel.SetFilter((int)channel.Id, bandIndex, p);
                }
            }
        }
    }

    #endregion

    #region Menu Handlers

    #region Preset Handlers

    private void RefreshPresetComboBox()
    {
        _isUpdatingPresetCombo = true;
        PresetComboBox.Items.Clear();

        if (!ViewModel.PresetsSupported)
        {
            PresetSection.Visibility = Visibility.Collapsed;
            _isUpdatingPresetCombo = false;
            return;
        }

        PresetSection.Visibility = ViewModel.IsDeviceConnected ? Visibility.Visible : Visibility.Collapsed;

        double maxWidth = 0;
        for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
        {
            var displayName = ViewModel.GetPresetDisplayName(i);
            PresetComboBox.Items.Add(new ComboBoxItem
            {
                Content = displayName,
                Tag = i
            });

            var tb = new TextBlock { Text = displayName, FontSize = PresetComboBox.FontSize };
            tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (tb.DesiredSize.Width > maxWidth)
                maxWidth = tb.DesiredSize.Width;
        }

        // Add padding for the ComboBox chrome (dropdown arrow + internal padding)
        PresetComboBox.MinWidth = maxWidth + 48;

        if (ViewModel.ActivePreset >= 0 && ViewModel.ActivePreset < MainViewModel.PresetSlotCount)
            PresetComboBox.SelectedIndex = ViewModel.ActivePreset;
        else
            PresetComboBox.SelectedIndex = -1;

        _isUpdatingPresetCombo = false;
    }

    private async void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingPresetCombo) return;
        if (PresetComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not int slot) return;

        if (!ViewModel.IsDeviceConnected) return;

        // Selecting an empty slot → offer to save
        if (!ViewModel.IsPresetOccupied(slot))
        {
            await SaveToPresetSlot(slot);
            return;
        }

        // If dirty, ask about unsaved changes
        if (ViewModel.PresetsDirty && ViewModel.ActivePreset >= 0)
        {
            var summary = ViewModel.GetChangeSummary();
            var message = summary != null
                ? $"You have unsaved changes to the current preset:\n\n{summary}"
                : "You have unsaved changes to the current preset.";

            var dialog = new ContentDialog
            {
                Title = "Unsaved Changes",
                Content = message,
                PrimaryButtonText = "Save & Switch",
                SecondaryButtonText = "Discard & Switch",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Save current preset first
                var saveResult = await ViewModel.SavePreset(ViewModel.ActivePreset, null);
                if (saveResult != Usb.PresetResult.Ok)
                {
                    await ShowErrorDialog("Failed to save current preset");
                    RevertPresetCombo();
                    return;
                }
            }
            else if (result == ContentDialogResult.None)
            {
                // Cancel — revert combo
                RevertPresetCombo();
                return;
            }
        }

        // Load the selected preset
        var loadResult = await ViewModel.LoadPreset(slot);
        if (loadResult != Usb.PresetResult.Ok)
        {
            await ShowErrorDialog("Failed to load preset");
            RevertPresetCombo();
        }
    }

    private void RevertPresetCombo()
    {
        _isUpdatingPresetCombo = true;
        if (ViewModel.ActivePreset >= 0 && ViewModel.ActivePreset < PresetComboBox.Items.Count)
            PresetComboBox.SelectedIndex = ViewModel.ActivePreset;
        else
            PresetComboBox.SelectedIndex = -1;
        _isUpdatingPresetCombo = false;
    }

    private void OnPresetComboRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var flyout = new MenuFlyout();

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Save",
            Icon = new FontIcon { Glyph = "\uE74E" }
        });
        ((MenuFlyoutItem)flyout.Items[0]).Click += async (s, _) => await QuickSavePreset();

        if (ViewModel.ActivePreset >= 0 && ViewModel.IsPresetOccupied(ViewModel.ActivePreset))
        {
            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Reload",
                Icon = new FontIcon { Glyph = "\uE72C" }
            });
            ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) =>
            {
                var result = await ViewModel.LoadPreset(ViewModel.ActivePreset);
                if (result != Usb.PresetResult.Ok)
                    await ShowErrorDialog("Failed to reload preset");
            };

            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Rename...",
                Icon = new FontIcon { Glyph = "\uE8AC" }
            });
            ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) => await ShowRenamePresetDialog(ViewModel.ActivePreset);

            flyout.Items.Add(new MenuFlyoutItem
            {
                Text = "Clear This Preset",
                Icon = new FontIcon { Glyph = "\uE74D" }
            });
            ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Clear Preset",
                    Content = $"Delete \"{ViewModel.GetPresetName(ViewModel.ActivePreset)}\"?",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    var result = await ViewModel.DeletePreset(ViewModel.ActivePreset);
                    if (result != Usb.PresetResult.Ok)
                        await ShowErrorDialog("Failed to delete preset");
                }
            };
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Clear All Presets",
            Icon = new FontIcon { Glyph = "\uE750" }
        });
        ((MenuFlyoutItem)flyout.Items[^1]).Click += async (s, _) =>
        {
            var dialog = new ContentDialog
            {
                Title = "Clear All Presets",
                Content = "Delete all presets from device flash? This cannot be undone.",
                PrimaryButtonText = "Delete All",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var result = await ViewModel.ClearAllPresets();
                if (result != Usb.PresetResult.Ok)
                    await ShowErrorDialog("Failed to clear presets");
            }
        };

        flyout.ShowAt(PresetComboBox, new FlyoutShowOptions
        {
            Position = e.GetPosition(PresetComboBox)
        });
    }

    private async Task QuickSavePreset()
    {
        if (!ViewModel.IsDeviceConnected) return;

        if (ViewModel.ActivePreset >= 0)
        {
            // Quick-save to active slot
            var result = await ViewModel.SavePreset(ViewModel.ActivePreset, null);
            if (result != Usb.PresetResult.Ok)
                await ShowErrorDialog("Failed to save preset");
        }
        else
        {
            // No active preset — show slot picker
            await ShowSaveToSlotDialog();
        }
    }

    private async Task SaveToPresetSlot(int slot)
    {
        // Prompt for name
        var nameBox = new TextBox
        {
            PlaceholderText = $"Preset {slot + 1}",
            MaxLength = 31,
            Text = ViewModel.IsPresetOccupied(slot) ? ViewModel.GetPresetName(slot) : ""
        };

        var dialog = new ContentDialog
        {
            Title = $"Save to Preset {slot + 1}",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = string.IsNullOrWhiteSpace(nameBox.Text) ? $"Preset {slot + 1}" : nameBox.Text.Trim();
            var result = await ViewModel.SavePreset(slot, name);
            if (result != Usb.PresetResult.Ok)
                await ShowErrorDialog("Failed to save preset");
        }
        else
        {
            RevertPresetCombo();
        }
    }

    private async Task ShowSaveToSlotDialog()
    {
        var panel = new StackPanel { Spacing = 12 };

        var slotCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < MainViewModel.PresetSlotCount; i++)
        {
            slotCombo.Items.Add(new ComboBoxItem
            {
                Content = ViewModel.GetPresetDisplayName(i),
                Tag = i
            });
        }
        slotCombo.SelectedIndex = 0;

        var nameBox = new TextBox
        {
            PlaceholderText = "Preset name",
            MaxLength = 31
        };

        panel.Children.Add(new TextBlock { Text = "Save to slot:" });
        panel.Children.Add(slotCombo);
        panel.Children.Add(nameBox);

        var dialog = new ContentDialog
        {
            Title = "Save Preset",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (slotCombo.SelectedItem is ComboBoxItem item && item.Tag is int slot)
            {
                var name = string.IsNullOrWhiteSpace(nameBox.Text) ? $"Preset {slot + 1}" : nameBox.Text.Trim();
                var result = await ViewModel.SavePreset(slot, name);
                if (result != Usb.PresetResult.Ok)
                    await ShowErrorDialog("Failed to save preset");
            }
        }
    }

    private async Task ShowRenamePresetDialog(int slot)
    {
        var nameBox = new TextBox
        {
            Text = ViewModel.GetPresetName(slot),
            MaxLength = 31
        };
        nameBox.SelectAll();

        var dialog = new ContentDialog
        {
            Title = "Rename Preset",
            Content = nameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var name = nameBox.Text.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                var ok = await ViewModel.RenamePreset(slot, name);
                if (!ok) await ShowErrorDialog("Failed to rename preset");
            }
        }
    }

    private async void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }

        if (ViewModel.PresetsSupported)
        {
            await QuickSavePreset();
        }
        else
        {
            // Legacy: fall back to SaveParams
            var flashResult = await ViewModel.SaveParams();
            if (flashResult == Usb.FlashResult.Ok)
                await ShowSuccessDialog("Parameters saved successfully");
            else
                await ShowErrorDialog("Failed to save parameters");
        }
    }

    private async void OnRevertPresetClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            await ShowErrorDialog("Not connected to device");
            return;
        }

        if (ViewModel.PresetsSupported && ViewModel.ActivePreset >= 0)
        {
            var summary = ViewModel.GetChangeSummary();
            var message = summary != null
                ? $"Revert to saved \"{ViewModel.GetPresetName(ViewModel.ActivePreset)}\"?\n\nPending changes:\n{summary}"
                : $"Revert to saved \"{ViewModel.GetPresetName(ViewModel.ActivePreset)}\"?";

            var dialog = new ContentDialog
            {
                Title = "Revert Preset",
                Content = message,
                PrimaryButtonText = "Revert",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var result = await ViewModel.LoadPreset(ViewModel.ActivePreset);
                if (result != Usb.PresetResult.Ok)
                    await ShowErrorDialog("Failed to revert preset");
            }
        }
        else
        {
            // Legacy: fall back to LoadParams
            var dialog = new ContentDialog
            {
                Title = "Revert to Saved",
                Content = "Revert to last saved parameters?\n\nCurrent unsaved changes will be lost.",
                PrimaryButtonText = "Revert",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var flashResult = await ViewModel.LoadParams();
                switch (flashResult)
                {
                    case Usb.FlashResult.Ok:
                        break;
                    case Usb.FlashResult.ErrNoData:
                        await ShowInfoDialog("No saved parameters found.\n\nThe device is using factory defaults.");
                        break;
                    default:
                        await ShowErrorDialog("Failed to load parameters");
                        break;
                }
            }
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed) return;

        if (!ViewModel.PresetsDirty || !ViewModel.IsDeviceConnected)
            return;

        args.Cancel = true;

        var summary = ViewModel.GetChangeSummary();
        var message = summary != null
            ? $"You have unsaved changes:\n\n{summary}"
            : "You have unsaved changes.";

        var dialog = new ContentDialog
        {
            Title = "Unsaved Changes",
            Content = message,
            PrimaryButtonText = ViewModel.PresetsSupported && ViewModel.ActivePreset >= 0 ? "Save & Quit" : "Quit",
            SecondaryButtonText = ViewModel.PresetsSupported && ViewModel.ActivePreset >= 0 ? "Discard & Quit" : null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && ViewModel.PresetsSupported && ViewModel.ActivePreset >= 0)
        {
            var saveResult = await ViewModel.SavePreset(ViewModel.ActivePreset, null);
            if (saveResult != Usb.PresetResult.Ok)
            {
                await ShowErrorDialog("Failed to save preset. Close anyway?");
            }
            _closeConfirmed = true;
            Close();
        }
        else if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
        {
            _closeConfirmed = true;
            Close();
        }
    }

    #endregion

    private async void OnFactoryResetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Factory Reset",
            Content = "Do you wish to clear all active parameters?\n\nThis will not overwrite your saved presets.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (!ViewModel.IsDeviceConnected)
            {
                await ShowErrorDialog("Not connected to device");
                return;
            }

            var flashResult = await ViewModel.FactoryResetParams();
            if (flashResult == Usb.FlashResult.Ok)
            {
                await ShowSuccessDialog("Factory reset complete");
            }
            else
            {
                await ShowErrorDialog("Failed to reset parameters");
            }
        }
    }

    private async Task ShowSuccessDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Success",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task ShowErrorDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Error",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task ShowInfoDialog(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Information",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void OnLoudnessClick(object sender, RoutedEventArgs e)
    {
        if (_loudnessWindow == null)
        {
            _loudnessWindow = new LoudnessWindow(ViewModel);
            _loudnessWindow.Closed += (s, e) => _loudnessWindow = null;
        }
        _loudnessWindow.Activate();
    }

    private void OnCrossfeedClick(object sender, RoutedEventArgs e)
    {
        if (_crossfeedWindow == null)
        {
            _crossfeedWindow = new CrossfeedWindow(ViewModel);
            _crossfeedWindow.Closed += (s, e) => _crossfeedWindow = null;
        }
        _crossfeedWindow.Activate();
    }

    private async void OnMatrixMixerClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsDeviceConnected)
        {
            var dialog = new ContentDialog
            {
                Title = "Device Not Connected",
                Content = "Please connect a DSPi device first.",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }

        if (_matrixMixerWindow == null)
        {
            _matrixMixerWindow = new MatrixMixerWindow(ViewModel);
            _matrixMixerWindow.Closed += (s, e) => _matrixMixerWindow = null;
        }
        _matrixMixerWindow.Activate();
    }

    private void OnStatsClick(object sender, RoutedEventArgs e)
    {
        if (_statsWindow == null)
        {
            _statsWindow = new StatsWindow(ViewModel.Device);
            _statsWindow.Closed += (s, e) => _statsWindow = null;
        }
        _statsWindow.Activate();
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(ViewModel) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    private async void OnAutoEQUpdateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Update AutoEQ Database",
            Content = "Choose how to update the AutoEQ database:",
            PrimaryButtonText = "Import File",
            SecondaryButtonText = "Reset to Built-in",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            // Import file
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                try
                {
                    var json = await Windows.Storage.FileIO.ReadTextAsync(file);
                    // Validate by attempting to deserialize
                    var testParse = System.Text.Json.JsonSerializer.Deserialize<AutoEQDatabase>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (testParse?.Entries == null || testParse.Entries.Count == 0)
                    {
                        await ShowErrorDialog("Invalid database file: no entries found.");
                        return;
                    }

                    var appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPiConsole");
                    Directory.CreateDirectory(appDataPath);
                    var destPath = System.IO.Path.Combine(appDataPath, "autoeq_database.json");
                    File.WriteAllText(destPath, json);

                    AutoEQManager.Instance.LoadFromJson(json);
                    RefreshAutoEQFavoritesMenu();
                    await ShowSuccessDialog($"Database imported: {testParse.Entries.Count} entries loaded.");
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog($"Failed to import database: {ex.Message}");
                }
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            // Reset to built-in
            var appDataPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPiConsole");
            var userDbPath = System.IO.Path.Combine(appDataPath, "autoeq_database.json");
            if (File.Exists(userDbPath))
            {
                File.Delete(userDbPath);
            }
            await AutoEQManager.Instance.LoadDatabaseAsync();
            RefreshAutoEQFavoritesMenu();
            await ShowSuccessDialog("Database reset to built-in version.");
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    #region Import/Export Handlers

    private async void OnImportFiltersClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".txt");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        try
        {
            var contents = await Windows.Storage.FileIO.ReadTextAsync(file);
            var result = FilterFileService.ParseFile(contents);

            if (result.Format == FilterFileFormat.Unknown)
            {
                await ShowErrorDialog("Could not parse filter file. Unsupported format.");
                return;
            }

            if (result.Format == FilterFileFormat.DSPiConsole && result.ChannelFilters != null)
            {
                await ImportMultiChannelFilters(result.ChannelFilters);
            }
            else if (result.Format == FilterFileFormat.REW && result.SingleChannelFilters != null)
            {
                await ImportSingleChannelFilters(result.SingleChannelFilters);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Failed to read file: {ex.Message}");
        }
    }

    private async Task ImportSingleChannelFilters(List<FilterParams> filters)
    {
        var dialog = new ChannelSelectionDialog { XamlRoot = Content.XamlRoot };
        dialog.ConfigureForSingleChannel(filters.Count, ViewModel.ActiveOutputs, ViewModel.IsOutputEnabled);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            dialog.CollectSelectedChannels();
            foreach (var channelId in dialog.SelectedChannelIds)
            {
                if (!await ApplyFiltersToChannel(channelId, filters))
                {
                    await ShowErrorDialog("Communication Failure - Unable to perform operation");
                    return;
                }
            }

            if (dialog.SelectedChannelIds.Count > 0)
            {
                await ShowSuccessDialog($"Filters imported to {dialog.SelectedChannelIds.Count} channel(s)");
            }
        }
    }

    private async Task ImportMultiChannelFilters(Dictionary<int, List<FilterParams>> channelFilters)
    {
        var dialog = new ChannelSelectionDialog { XamlRoot = Content.XamlRoot };
        dialog.ConfigureForMultiChannel(channelFilters.Keys, ViewModel.ActiveOutputs, ViewModel.IsOutputEnabled);

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            dialog.CollectSelectedChannels();
            foreach (var channelId in dialog.SelectedChannelIds)
            {
                if (channelFilters.TryGetValue(channelId, out var filters))
                {
                    if (!await ApplyFiltersToChannel(channelId, filters))
                    {
                        await ShowErrorDialog("Communication Failure - Unable to perform operation");
                        return;
                    }
                }
            }

            if (dialog.SelectedChannelIds.Count > 0)
            {
                await ShowSuccessDialog("Filters imported successfully");
            }
        }
    }

    private async Task<bool> ApplyFiltersToChannel(int channelId, List<FilterParams> filters)
    {
        var channel = Channel.All.FirstOrDefault(c => (int)c.Id == channelId);
        if (channel == null) return false;

        var bandCount = channel.BandCount;

        // Apply imported filters
        for (int i = 0; i < Math.Min(filters.Count, bandCount); i++)
        {
            if (!await SetFilterWithRetry(channelId, i, filters[i].Clone()))
                return false;
        }

        // Clear remaining bands
        for (int i = filters.Count; i < bandCount; i++)
        {
            if (!await SetFilterWithRetry(channelId, i, new FilterParams(FilterType.Flat, 1000, 0.707f, 0)))
                return false;
        }

        return true;
    }

    private async Task<bool> SetFilterWithRetry(int channelId, int band, FilterParams p)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (await ViewModel.SetFilter(channelId, band, p))
                return true;
        }
        return false;
    }

    private async void OnExportFiltersClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = "DSPi Filters";
        picker.FileTypeChoices.Add("Text Files", new List<string> { ".txt" });

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            // Build channel data dictionary
            var channelData = new Dictionary<int, IReadOnlyList<FilterParams>>();
            foreach (var channel in Channel.All)
            {
                var filters = ViewModel.GetFilters(channel);
                channelData[(int)channel.Id] = filters.ToList();
            }

            var output = FilterFileService.GenerateExportString(channelData);
            await Windows.Storage.FileIO.WriteTextAsync(file, output);
            await ShowSuccessDialog("Filters exported successfully");
        }
        catch (Exception ex)
        {
            await ShowErrorDialog($"Failed to write file: {ex.Message}");
        }
    }

    #endregion

    #region AutoEQ Handlers

    private async void OnAutoEQBrowseClick(object sender, RoutedEventArgs e)
    {
        // Ensure database is loaded
        if (!AutoEQManager.Instance.IsLoaded)
        {
            await AutoEQManager.Instance.LoadDatabaseAsync();
        }

        if (!AutoEQManager.Instance.IsLoaded)
        {
            await ShowErrorDialog(AutoEQManager.Instance.ErrorMessage ?? "Failed to load AutoEQ database");
            return;
        }

        var dialog = new AutoEQBrowserDialog { XamlRoot = Content.XamlRoot };
        var result = await dialog.ShowAsync();

        // Always refresh favorites menu after dialog closes (user may have added/removed favorites)
        RefreshAutoEQFavoritesMenu();

        if (result == ContentDialogResult.Primary && dialog.SelectedProfile != null)
        {
            if (!await ApplyAutoEQProfile(dialog.SelectedProfile))
            {
                await ShowErrorDialog("Communication Failure - Unable to perform operation");
                return;
            }
            await ShowSuccessDialog($"Applied profile: {dialog.SelectedProfile.DisplayName}");
        }
    }

    private async Task<bool> ApplyAutoEQProfile(HeadphoneEntry profile)
    {
        var filters = AutoEQManager.ConvertFilters(profile);

        var dialog = new ChannelSelectionDialog { XamlRoot = Content.XamlRoot };
        dialog.ConfigureForAutoEQ(
            filters.Count,
            ViewModel.ActiveOutputs,
            ViewModel.IsOutputEnabled,
            ch => ViewModel.GetChannelName(ch));

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return true; // user cancelled

        dialog.CollectSelectedChannels();
        if (dialog.SelectedChannelIds.Count == 0) return true;

        // Set preamp only after user confirms
        ViewModel.PreampDb = (float)profile.Preamp;

        // Apply filters to each selected channel
        foreach (var channelId in dialog.SelectedChannelIds)
        {
            if (!await ApplyFiltersToChannel(channelId, filters))
                return false;
        }

        // Refresh editor if selected channel was affected
        if (_selectedChannel != null &&
            dialog.SelectedChannelIds.Contains((int)_selectedChannel.Id))
            ShowChannelEditor(_selectedChannel);

        return true;
    }

    private void RefreshAutoEQFavoritesMenu()
    {
        PopulateFavoritesMenu(AutoEQFavoritesMenu);
    }

    private void PopulateFavoritesMenu(MenuFlyoutSubItem menu)
    {
        menu.Items.Clear();

        var favorites = AutoEQManager.Instance.Favorites;
        if (favorites.Count == 0)
        {
            var emptyItem = new MenuFlyoutItem
            {
                Text = "No favorites yet",
                IsEnabled = false
            };
            menu.Items.Add(emptyItem);
        }
        else
        {
            foreach (var entry in favorites)
            {
                var item = new MenuFlyoutItem { Text = entry.DisplayName, Tag = entry };
                item.Click += OnAutoEQFavoriteClick;
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var clearItem = new MenuFlyoutItem { Text = "Clear Favorites" };
            clearItem.Click += async (s, e) =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Clear Favorites",
                    Content = "Are you sure you want to clear all AutoEQ favorites?",
                    PrimaryButtonText = "Clear",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    AutoEQManager.Instance.ClearFavorites();
                    RefreshAutoEQFavoritesMenu();
                }
            };
            menu.Items.Add(clearItem);
        }
    }

    private async void OnAutoEQFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is HeadphoneEntry profile)
        {
            if (!await ApplyAutoEQProfile(profile))
            {
                await ShowErrorDialog("Communication Failure - Unable to perform operation");
                return;
            }
            await ShowSuccessDialog($"Applied profile: {profile.DisplayName}");
        }
    }

    #endregion

    #region Graph Resize

    private RowDefinition GraphRow => ContentGrid.RowDefinitions[1];

    private void OnGraphGripperPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el)
        {
            _isResizingGraph = true;
            _graphResizeStartY = e.GetCurrentPoint(ContentGrid).Position.Y;
            _graphResizeStartHeight = GraphRow.Height.Value;
            el.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnGraphGripperPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingGraph) return;
        var delta = e.GetCurrentPoint(ContentGrid).Position.Y - _graphResizeStartY;
        var newHeight = Math.Clamp(_graphResizeStartHeight + delta, GraphMinHeight, GraphMaxHeight);
        GraphRow.Height = new GridLength(newHeight);
        e.Handled = true;
    }

    private void OnGraphGripperPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingGraph) return;
        _isResizingGraph = false;
        if (sender is UIElement el)
            el.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    #endregion

    #region Graph Popout

    private DispatcherTimer? _popoutFadeTimer;
    private double _popoutFadeTarget;

    private void OnGraphAreaPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        GraphPopoutButton.IsHitTestVisible = true;
        FadePopoutButton(0.6);
    }

    private void OnGraphAreaPointerExited(object sender, PointerRoutedEventArgs e)
    {
        GraphPopoutButton.IsHitTestVisible = false;
        FadePopoutButton(0);
    }

    private void FadePopoutButton(double target)
    {
        _popoutFadeTarget = target;
        if (_popoutFadeTimer == null)
        {
            _popoutFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _popoutFadeTimer.Tick += (_, _) =>
            {
                double diff = _popoutFadeTarget - GraphPopoutButton.Opacity;
                if (Math.Abs(diff) < 0.02)
                {
                    GraphPopoutButton.Opacity = _popoutFadeTarget;
                    _popoutFadeTimer.Stop();
                }
                else
                {
                    GraphPopoutButton.Opacity += diff * 0.25;
                }
            };
        }
        _popoutFadeTimer.Start();
    }

    private void OnGraphPopoutClick(object sender, RoutedEventArgs e)
    {
        if (_graphWindow != null)
        {
            _graphWindow.Activate();
            return;
        }

        // Animate graph row collapsing
        GraphHeader.Visibility = Visibility.Collapsed;
        GraphGripperControl.Visibility = Visibility.Collapsed;
        LegendPanel.Visibility = Visibility.Collapsed;
        AnimateGraphRow(GraphRow.Height.Value, 0, 250, () =>
        {
            GraphArea.Visibility = Visibility.Collapsed;
            GraphRow.Height = GridLength.Auto;
        });

        // Open popout window
        _graphWindow = new GraphWindow(ViewModel);
        bool follows = AppSettings.Instance.PopoutFollowsSelectedChannel;
        _graphWindow.SetIgnoreVisibility(!follows);
        if (_selectedChannel != null && follows)
            _graphWindow.SetSelectedChannel((int)_selectedChannel.Id);
        _graphWindow.Closed += (_, _) =>
        {
            _graphWindow = null;

            // Restore and animate graph row expanding
            GraphArea.Visibility = Visibility.Visible;
            GraphArea.Opacity = 0;
            GraphHeader.Visibility = Visibility.Visible;
            GraphHeader.Opacity = 0;
            LegendPanel.Visibility = Visibility.Visible;
            LegendPanel.Opacity = 0;
            GraphRow.Height = new GridLength(0);

            AnimateGraphRow(0, 250, 300, () =>
            {
                GraphGripperControl.Visibility = Visibility.Visible;
                GraphArea.Opacity = 1;
                GraphHeader.Opacity = 1;
                LegendPanel.Opacity = 1;
            });
        };
        _graphWindow.Activate();
    }

    private void AnimateGraphRow(double from, double to, int durationMs, Action? onComplete = null)
    {
        const int frameMs = 16;
        int totalFrames = Math.Max(1, durationMs / frameMs);
        int frame = 0;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(frameMs) };
        timer.Tick += (_, _) =>
        {
            frame++;
            double t = Math.Min(1.0, (double)frame / totalFrames);
            // Ease out cubic
            double eased = 1.0 - Math.Pow(1.0 - t, 3);
            double height = from + (to - from) * eased;
            GraphRow.Height = new GridLength(Math.Max(0, height));

            // Fade graph area, header and legend proportionally
            double opacity = to > from ? eased : 1.0 - eased;
            GraphArea.Opacity = opacity;
            GraphHeader.Opacity = opacity;
            LegendPanel.Opacity = opacity;

            if (t >= 1.0)
            {
                timer.Stop();
                onComplete?.Invoke();
            }
        };
        timer.Start();
    }

    #endregion

    #region Title Bar

    private void UpdateTitleBarDragRegion()
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        var buttonPos = TitleBarMenuButton.TransformToVisual(AppTitleBar).TransformPoint(new Windows.Foundation.Point(0, 0));

        int titleBarWidth = (int)(AppTitleBar.ActualWidth * scale);
        int titleBarHeight = (int)(AppTitleBar.ActualHeight * scale);
        int btnX = (int)(buttonPos.X * scale);
        int btnW = (int)(TitleBarMenuButton.ActualWidth * scale);

        // Two drag rectangles: left of button and right of button
        var left = new Windows.Graphics.RectInt32(0, 0, btnX, titleBarHeight);
        var right = new Windows.Graphics.RectInt32(btnX + btnW, 0, titleBarWidth - btnX - btnW, titleBarHeight);

        var nonClientInput = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)));
        nonClientInput.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough,
            new[] { new Windows.Graphics.RectInt32(btnX, (int)(buttonPos.Y * scale), btnW, (int)(TitleBarMenuButton.ActualHeight * scale)) });
    }

    #endregion
}
