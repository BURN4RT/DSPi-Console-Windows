using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DSPiConsole.Controls;

/// <summary>
/// Horizontal meter bar for displaying audio levels with clip indicator.
/// </summary>
public sealed class HorizontalMeterBar : UserControl
{
    private readonly Border _background;
    private readonly Border _foreground;
    private readonly Border _clipIndicator;
    private readonly Grid _meterGrid;
    private readonly DispatcherTimer _smoothingTimer;
    private readonly Storyboard _clipFadeStoryboard;
    private readonly DoubleAnimation _clipFadeAnimation;
    private double _currentLevel;
    private double _targetLevel;

    private const double ClipZoneWidth = 3;

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(double), typeof(HorizontalMeterBar),
            new PropertyMetadata(0.0, OnLevelChanged));

    public static readonly DependencyProperty MeterColorProperty =
        DependencyProperty.Register(nameof(MeterColor), typeof(Color), typeof(HorizontalMeterBar),
            new PropertyMetadata(Colors.DodgerBlue, OnMeterColorChanged));

    public static readonly DependencyProperty IsClippingProperty =
        DependencyProperty.Register(nameof(IsClipping), typeof(bool), typeof(HorizontalMeterBar),
            new PropertyMetadata(false, OnIsClippingChanged));

    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(nameof(IsMuted), typeof(bool), typeof(HorizontalMeterBar),
            new PropertyMetadata(false, OnIsMutedChanged));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Color MeterColor
    {
        get => (Color)GetValue(MeterColorProperty);
        set => SetValue(MeterColorProperty, value);
    }

    public bool IsClipping
    {
        get => (bool)GetValue(IsClippingProperty);
        set => SetValue(IsClippingProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public HorizontalMeterBar()
    {
        Height = 6;

        _meterGrid = new Grid();

        _background = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(2)
        };

        _foreground = new Border
        {
            Background = new SolidColorBrush(MeterColor),
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        _clipIndicator = new Border
        {
            Background = new SolidColorBrush(Colors.Red),
            CornerRadius = new CornerRadius(1),
            Width = ClipZoneWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0
        };

        _clipFadeAnimation = new DoubleAnimation
        {
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(_clipFadeAnimation, _clipIndicator);
        Storyboard.SetTargetProperty(_clipFadeAnimation, "Opacity");
        _clipFadeStoryboard = new Storyboard();
        _clipFadeStoryboard.Children.Add(_clipFadeAnimation);
        // Commit the animated end value to the local Opacity property so a
        // subsequent fade reads the correct starting value (storyboards write
        // to the "animated value" layer; the local DP otherwise stays at 0).
        _clipFadeStoryboard.Completed += (_, _) =>
            _clipIndicator.Opacity = _clipFadeAnimation.To ?? 0;

        _meterGrid.Children.Add(_background);
        _meterGrid.Children.Add(_foreground);
        _meterGrid.Children.Add(_clipIndicator);

        Content = _meterGrid;

        SizeChanged += (s, e) => UpdateMeterWidth();

        // Smoothing timer at ~60fps
        _smoothingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _smoothingTimer.Tick += OnSmoothingTick;
        _smoothingTimer.Start();
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HorizontalMeterBar meter)
        {
            meter._targetLevel = (double)e.NewValue;
        }
    }

    private static void OnMeterColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HorizontalMeterBar meter)
        {
            meter._foreground.Background = new SolidColorBrush((Color)e.NewValue);
        }
    }

    private static void OnIsClippingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HorizontalMeterBar meter)
        {
            meter.AnimateClipIndicator((bool)e.NewValue);
        }
    }

    private void AnimateClipIndicator(bool clipping)
    {
        _clipFadeStoryboard.Stop();
        _clipFadeAnimation.From = _clipIndicator.Opacity;
        _clipFadeAnimation.To = clipping ? 1.0 : 0.0;
        _clipFadeStoryboard.Begin();
    }

    private static void OnIsMutedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HorizontalMeterBar meter)
        {
            meter.Opacity = (bool)e.NewValue ? 0.4 : 1.0;
        }
    }

    private void OnSmoothingTick(object? sender, object e)
    {
        // Lerp towards target with different speeds for attack and decay
        double diff = _targetLevel - _currentLevel;

        if (diff > 0)
        {
            // Attack (rising) - faster response
            _currentLevel += diff * 0.4;
        }
        else
        {
            // Decay (falling) - slower response for smoother falloff
            _currentLevel += diff * 0.15;
        }

        // Snap to target if very close
        if (Math.Abs(diff) < 0.001)
        {
            _currentLevel = _targetLevel;
        }

        UpdateMeterWidth();
    }

    private void UpdateMeterWidth()
    {
        double level = Math.Max(0, Math.Min(1, _currentLevel));
        double availableWidth = Math.Max(0, ActualWidth - ClipZoneWidth);
        _foreground.Width = availableWidth * level;
    }
}
