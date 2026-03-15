using DSPiConsole.Core.Models;
using DSPiConsole.Models;
using DSPiConsole.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// Custom control for rendering Bode plot frequency response curves.
/// Uses a dual-canvas layout: _plotCanvas (clipped) for grid/curves, _labelCanvas (unclipped) for axis labels.
/// </summary>
public sealed class BodePlotControl : UserControl
{
    private Grid? _rootGrid;
    private Canvas? _plotCanvas;
    private Canvas? _labelCanvas;
    private Canvas? _dbScaleHitArea;
    private MainViewModel? _viewModel;
    private int _selectedChannelId = -1;
    private bool _dottedInactiveEnabled = true;
    private bool _isPopout;
    private bool _ignoreVisibility;
    private Dictionary<int, bool>? _localVisibility;

    /// <summary>
    /// Set the selected channel ID for dotted-line treatment of non-selected channels.
    /// Pass -1 (or any invalid ID) to show all channels as solid (dashboard mode).
    /// </summary>
    public void SetSelectedChannel(int channelId)
    {
        if (_selectedChannelId == channelId) return;
        _selectedChannelId = channelId;
        Redraw(gridChanged: true);
    }

    /// <summary>
    /// Enable or disable dotted lines for non-selected channels.
    /// </summary>
    public void SetDottedInactiveEnabled(bool enabled)
    {
        if (_dottedInactiveEnabled == enabled) return;
        _dottedInactiveEnabled = enabled;
        Redraw(gridChanged: true);
    }

    /// <summary>
    /// Mark this control as the popout instance so it reads the correct setting.
    /// </summary>
    public void SetIsPopout(bool isPopout)
    {
        _isPopout = isPopout;
        _dottedInactiveEnabled = AppSettings.Instance.DottedInactiveChannels;
    }

    /// <summary>
    /// When true, use local visibility state instead of ViewModel's.
    /// Used by the popout graph when "follows selected channel" is off.
    /// </summary>
    public void SetIgnoreVisibility(bool ignore, MainViewModel? viewModelOverride = null)
    {
        if (_ignoreVisibility == ignore) return;
        _ignoreVisibility = ignore;
        var vm = viewModelOverride ?? _viewModel;
        if (ignore && vm != null)
        {
            // Snapshot current ViewModel visibility state
            _localVisibility = new Dictionary<int, bool>();
            foreach (var ch in Channel.All)
                _localVisibility[(int)ch.Id] = vm.GetChannelVisibility(ch);
        }
        else
        {
            _localVisibility = null;
        }
        Redraw(gridChanged: true);
    }

    /// <summary>
    /// Toggle visibility in local mode. Used by popout legend pills when ignoring ViewModel visibility.
    /// </summary>
    public void ToggleLocalVisibility(int channelId)
    {
        if (_localVisibility == null) return;
        _localVisibility[channelId] = !(_localVisibility.TryGetValue(channelId, out var v) && v);
        Redraw(gridChanged: true);
    }

    public bool GetLocalVisibility(int channelId) =>
        _localVisibility == null || !_localVisibility.TryGetValue(channelId, out var v) || v;

    private const int NumPoints = 201;

    // Plot area margins (px)
    private double LeftMargin => AppSettings.Instance.ShowDbUnits ? 36 : 22;
    private const double BottomMargin = 16;
    private const double TopMargin = 9;
    private const double RightMargin = 8;

    // Settings-derived properties
    private float MinFreq => (float)AppSettings.Instance.GraphMinFrequency;
    private float MaxFreq => (float)AppSettings.Instance.GraphMaxFrequency;
    private float DbTop => (float)(AppSettings.Instance.GraphDbCenter + AppSettings.Instance.GraphDbRange / 2.0);
    private float DbBottom => (float)(AppSettings.Instance.GraphDbCenter - AppSettings.Instance.GraphDbRange / 2.0);
    private float DbSpan => (float)AppSettings.Instance.GraphDbRange;

    // Fixed frequency set for the data pipeline (201 points, 20–20kHz log-spaced)
    private const float DataMinFreq = 10.0f;
    private const float DataMaxFreq = 20000.0f;

    // Animation state
    private readonly Dictionary<int, float[]> _currentMagnitudes = new();
    private readonly Dictionary<int, float[]> _targetMagnitudes = new();
    private readonly DispatcherTimer _animTimer;
    private bool _isAnimating;

    // Curve fade opacity (1 = visible, 0 = hidden)
    private double _curveOpacity = 1.0;

    // Cached polyline references per channel (for glow: 3 per channel, otherwise 1)
    private readonly Dictionary<int, List<Polyline>> _channelPolylines = new();

    public BodePlotControl()
    {
        _rootGrid = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(128, 32, 32, 36))
        };

        _plotCanvas = new Canvas();
        _labelCanvas = new Canvas { IsHitTestVisible = false };
        _dbScaleHitArea = new Canvas
        {
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = LeftMargin
        };
        _dbScaleHitArea.PointerWheelChanged += OnDbScalePointerWheelChanged;

        _rootGrid.Children.Add(_plotCanvas);
        _rootGrid.Children.Add(_labelCanvas);
        _rootGrid.Children.Add(_dbScaleHitArea);
        Content = _rootGrid;

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += OnAnimationTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            _viewModel = vm;
            _viewModel.FiltersChanged += OnFiltersChanged;
            _viewModel.VisibilityChanged += OnVisibilityChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            AppSettings.Instance.SettingsChanged += OnSettingsChanged;

            foreach (var channel in Channel.All)
            {
                var id = (int)channel.Id;
                _currentMagnitudes[id] = new float[NumPoints];
                _targetMagnitudes[id] = new float[NumPoints];
            }

            UpdateTargets();
            foreach (var channel in Channel.All)
            {
                var id = (int)channel.Id;
                Array.Copy(_targetMagnitudes[id], _currentMagnitudes[id], NumPoints);
            }
            Redraw(gridChanged: true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _animTimer.Stop();
        _isAnimating = false;
        if (_viewModel != null)
        {
            _viewModel.FiltersChanged -= OnFiltersChanged;
            _viewModel.VisibilityChanged -= OnVisibilityChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        AppSettings.Instance.SettingsChanged -= OnSettingsChanged;
    }

    private void OnFiltersChanged(object? sender, EventArgs e)
    {
        UpdateTargets();
        StartAnimation();
    }

    private void OnVisibilityChanged(object? sender, EventArgs e) => Redraw(gridChanged: true);
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_dbScaleHitArea != null)
            _dbScaleHitArea.Width = LeftMargin;
        _dottedInactiveEnabled = AppSettings.Instance.DottedInactiveChannels;
        Redraw(gridChanged: true);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Bypass))
        {
            UpdateTargets();
            StartAnimation();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePlotClip();
        Redraw(gridChanged: true);
    }

    private void UpdatePlotClip()
    {
        if (_plotCanvas == null) return;
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double plotWidth = w - LeftMargin - RightMargin;
        double plotHeight = h - TopMargin - BottomMargin;
        if (plotWidth <= 0 || plotHeight <= 0) return;

        _plotCanvas.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(LeftMargin, TopMargin, plotWidth, plotHeight)
        };
    }

    private void UpdateTargets()
    {
        if (_viewModel == null) return;

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            var (_, magnitudes) = _viewModel.GetResponseCurve(channel);
            if (magnitudes.Length == NumPoints)
            {
                Array.Copy(magnitudes, _targetMagnitudes[id], NumPoints);
            }
            else if (magnitudes.Length > 0)
            {
                for (int i = 0; i < NumPoints; i++)
                {
                    float pct = i / (float)(NumPoints - 1);
                    int srcIdx = Math.Clamp((int)(pct * (magnitudes.Length - 1)), 0, magnitudes.Length - 1);
                    _targetMagnitudes[id][i] = magnitudes[srcIdx];
                }
            }
            else
            {
                Array.Clear(_targetMagnitudes[id]);
            }
        }
    }

    private void StartAnimation()
    {
        if (!_isAnimating)
        {
            _isAnimating = true;
            _animTimer.Start();
        }
    }

    private void OnAnimationTick(object? sender, object e)
    {
        float speed = (float)AppSettings.Instance.GraphAnimationSpeed;
        float lerpFactor = Math.Clamp(speed, 0.05f, 0.5f);
        bool allDone = true;

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            var current = _currentMagnitudes[id];
            var target = _targetMagnitudes[id];

            for (int i = 0; i < NumPoints; i++)
            {
                float diff = target[i] - current[i];
                if (MathF.Abs(diff) > 0.01f)
                {
                    current[i] += diff * lerpFactor;
                    allDone = false;
                }
                else
                {
                    current[i] = target[i];
                }
            }
        }

        Redraw(gridChanged: false);

        if (allDone)
        {
            _animTimer.Stop();
            _isAnimating = false;
        }
    }

    private double XPos(float freq, double plotWidth)
    {
        float logMin = MathF.Log10(MinFreq);
        float logMax = MathF.Log10(MaxFreq);
        float logVal = MathF.Log10(freq);
        return LeftMargin + (logVal - logMin) / (logMax - logMin) * plotWidth;
    }

    private double YPos(float db, double plotHeight)
    {
        float normalized = (db - DbBottom) / DbSpan;
        return TopMargin + plotHeight - (normalized * plotHeight);
    }

    public void Invalidate()
    {
        UpdateTargets();
        StartAnimation();
    }

    public double GetCurveOpacity() => _curveOpacity;

    public void SetCurveOpacity(double opacity)
    {
        _curveOpacity = Math.Clamp(opacity, 0.0, 1.0);
        foreach (var polylines in _channelPolylines.Values)
        {
            foreach (var p in polylines)
                p.Opacity = _curveOpacity;
        }
    }

    /// <summary>
    /// Compute the frequency for a given data point index (0..NumPoints-1) in the 20–20kHz log space.
    /// </summary>
    private static float DataFreqAt(int index)
    {
        float t = index / (float)(NumPoints - 1);
        float logMin = MathF.Log10(DataMinFreq);
        float logMax = MathF.Log10(DataMaxFreq);
        return MathF.Pow(10, logMin + t * (logMax - logMin));
    }

    private void Redraw(bool gridChanged)
    {
        if (_plotCanvas == null || _labelCanvas == null) return;

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        double plotWidth = width - LeftMargin - RightMargin;
        double plotHeight = height - TopMargin - BottomMargin;
        if (plotWidth <= 0 || plotHeight <= 0) return;

        if (gridChanged)
        {
            _plotCanvas.Children.Clear();
            _labelCanvas.Children.Clear();
            _channelPolylines.Clear();

            DrawGrid(plotWidth, plotHeight);
            DrawLabels(plotWidth, plotHeight);
            DrawCurves(plotWidth, plotHeight);
        }
        else
        {
            UpdateCurvePoints(plotWidth, plotHeight);
        }
    }

    private void DrawGrid(double plotWidth, double plotHeight)
    {
        var settings = AppSettings.Instance;

        // Frequency grid (vertical lines)
        if (settings.ShowFrequencyGrid)
        {
            var minorColor = Color.FromArgb(15, 255, 255, 255);
            var majorColor = Color.FromArgb(38, 255, 255, 255);

            // All decade subdivisions from 10 to 20000
            float[] decades = { 10, 100, 1000, 10000 };
            foreach (var decade in decades)
            {
                for (int m = 1; m <= 9; m++)
                {
                    float freq = decade * m;
                    if (freq < MinFreq || freq > MaxFreq) continue;

                    bool isMajor = m == 1 && freq >= 100;
                    double x = XPos(freq, plotWidth);

                    _plotCanvas!.Children.Add(new Line
                    {
                        X1 = x, Y1 = TopMargin,
                        X2 = x, Y2 = TopMargin + plotHeight,
                        Stroke = new SolidColorBrush(isMajor ? majorColor : minorColor),
                        StrokeThickness = 1
                    });
                }
            }
            // Also draw 20kHz if in range
            if (20000 <= MaxFreq && 20000 >= MinFreq)
            {
                double x = XPos(20000, plotWidth);
                _plotCanvas!.Children.Add(new Line
                {
                    X1 = x, Y1 = TopMargin,
                    X2 = x, Y2 = TopMargin + plotHeight,
                    Stroke = new SolidColorBrush(minorColor),
                    StrokeThickness = 1
                });
            }
        }

        // dB grid (horizontal lines)
        if (settings.ShowDbGrid)
        {
            var gridColor = Color.FromArgb(25, 255, 255, 255);
            var zeroLineColor = Color.FromArgb(76, 255, 255, 255);

            double step = GetDbStep();
            // Find first grid line at or above DbBottom
            double firstDb = Math.Ceiling(DbBottom / step) * step;

            for (double db = firstDb; db <= DbTop; db += step)
            {
                double y = YPos((float)db, plotHeight);
                bool isZero = Math.Abs(db) < 0.01;
                _plotCanvas!.Children.Add(new Line
                {
                    X1 = LeftMargin, Y1 = y,
                    X2 = LeftMargin + plotWidth, Y2 = y,
                    Stroke = new SolidColorBrush(isZero ? zeroLineColor : gridColor),
                    StrokeThickness = 1
                });
            }
        }
    }

    private void DrawLabels(double plotWidth, double plotHeight)
    {
        var settings = AppSettings.Instance;
        var labelColor = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255));

        // Frequency labels (bottom edge)
        if (settings.ShowFrequencyLabels)
        {
            float[] freqLabels = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
            foreach (var freq in freqLabels)
            {
                if (freq < MinFreq || freq > MaxFreq) continue;

                double x = XPos(freq, plotWidth);
                string text = FormatFrequency(freq);

                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = labelColor
                };

                // Measure and center horizontally
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double tbWidth = tb.DesiredSize.Width;

                Canvas.SetLeft(tb, x - tbWidth / 2);
                Canvas.SetTop(tb, TopMargin + plotHeight + 2);
                _labelCanvas!.Children.Add(tb);
            }
        }

        // dB labels (left edge)
        if (settings.ShowDbLabels)
        {
            double step = GetDbStep();
            double firstDb = Math.Ceiling(DbBottom / step) * step;

            for (double db = firstDb; db <= DbTop; db += step)
            {
                double y = YPos((float)db, plotHeight);

                // Skip labels that fall outside the plot area
                if (y < TopMargin - 4 || y > TopMargin + plotHeight + 4) continue;

                string text = FormatDb(db);
                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 9,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = labelColor
                };

                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double tbHeight = tb.DesiredSize.Height;
                double tbWidth = tb.DesiredSize.Width;

                Canvas.SetLeft(tb, LeftMargin - tbWidth - 4);
                Canvas.SetTop(tb, y - tbHeight / 2);
                _labelCanvas!.Children.Add(tb);
            }
        }
    }

    private void DrawCurves(double plotWidth, double plotHeight)
    {
        if (_viewModel == null) return;

        var settings = AppSettings.Instance;
        bool showGlow = settings.ShowGraphGlow;
        float lineWidth = (float)settings.GraphLineWidth;

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;

            if (_ignoreVisibility)
            {
                // Use local visibility; still hide disabled outputs
                if (!GetLocalVisibility(id))
                    continue;
                if (channel.IsOutput)
                {
                    int outputIndex = _viewModel.GetOutputIndex(id);
                    if (outputIndex < 0 || !_viewModel.IsOutputEnabled(outputIndex))
                        continue;
                }
            }
            else
            {
                if (!_viewModel.GetChannelVisibility(channel))
                    continue;
            }

            if (!_currentMagnitudes.ContainsKey(id)) continue;

            var magnitudes = _currentMagnitudes[id];
            var polylines = new List<Polyline>();

            var points = BuildPoints(magnitudes, plotWidth, plotHeight);
            bool isDotted = _dottedInactiveEnabled && _selectedChannelId >= 0 && id != _selectedChannelId;
            var dashArray = isDotted ? new DoubleCollection { 4, 3 } : null;

            if (showGlow && !isDotted)
            {
                var outerGlow = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(50, channel.Color.R, channel.Color.G, channel.Color.B)),
                    StrokeThickness = lineWidth * 4,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = ClonePoints(points)
                };
                _plotCanvas!.Children.Add(outerGlow);
                polylines.Add(outerGlow);

                var innerGlow = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(100, channel.Color.R, channel.Color.G, channel.Color.B)),
                    StrokeThickness = lineWidth * 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = ClonePoints(points)
                };
                _plotCanvas!.Children.Add(innerGlow);
                polylines.Add(innerGlow);
            }

            var mainLine = new Polyline
            {
                Stroke = new SolidColorBrush(channel.Color),
                StrokeThickness = lineWidth,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeDashArray = dashArray,
                Points = points
            };
            _plotCanvas!.Children.Add(mainLine);
            polylines.Add(mainLine);

            foreach (var p in polylines)
                p.Opacity = _curveOpacity;
            _channelPolylines[id] = polylines;
        }
    }

    private void UpdateCurvePoints(double plotWidth, double plotHeight)
    {
        if (_viewModel == null) return;

        foreach (var channel in Channel.All)
        {
            var id = (int)channel.Id;
            if (!_channelPolylines.ContainsKey(id)) continue;
            if (!_currentMagnitudes.ContainsKey(id)) continue;

            var magnitudes = _currentMagnitudes[id];
            var points = BuildPoints(magnitudes, plotWidth, plotHeight);

            foreach (var polyline in _channelPolylines[id])
            {
                polyline.Points = ClonePoints(points);
            }
        }
    }

    private PointCollection BuildPoints(float[] magnitudes, double plotWidth, double plotHeight)
    {
        var points = new PointCollection();
        for (int i = 0; i < NumPoints; i++)
        {
            float freq = DataFreqAt(i);
            double x = XPos(freq, plotWidth);
            double y = YPos(magnitudes[i], plotHeight);
            points.Add(new Windows.Foundation.Point(x, y));
        }
        return points;
    }

    private double GetDbStep()
    {
        double span = DbSpan;
        if (span <= 12) return 1;
        if (span <= 30) return 3;
        if (span <= 60) return 5;
        return 10;
    }

    private static string FormatFrequency(float freq)
    {
        if (freq >= 1000) return $"{freq / 1000:0.#}k";
        return freq.ToString("0");
    }

    private static string FormatDb(double db)
    {
        int rounded = (int)Math.Round(db);
        string unit = AppSettings.Instance.ShowDbUnits ? " dB" : "";
        if (rounded > 0) return $"+{rounded}{unit}";
        if (rounded < 0) return $"{rounded}{unit}";
        return $"0{unit}";
    }

    private void OnDbScalePointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        var settings = AppSettings.Instance;

        // Scroll up = zoom in (smaller range), scroll down = zoom out (larger range)
        double step = settings.GraphDbRange <= 20 ? 2 : 5;
        double newRange = delta > 0
            ? settings.GraphDbRange - step
            : settings.GraphDbRange + step;

        newRange = Math.Clamp(newRange, 10, 100);
        if (Math.Abs(newRange - settings.GraphDbRange) > 0.01)
        {
            settings.GraphDbRange = newRange;
            settings.NotifyChanged();
        }

        e.Handled = true;
    }

    private static PointCollection ClonePoints(PointCollection source)
    {
        var clone = new PointCollection();
        foreach (var pt in source)
            clone.Add(pt);
        return clone;
    }
}
