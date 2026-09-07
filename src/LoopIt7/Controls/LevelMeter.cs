using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace LoopIt7.Controls;

/// <summary>
/// A peak meter on a decibel scale, with a hold marker and a clip flag.
/// <para>
/// Ballistics live here rather than in the view model: the meter is the one place in the app
/// where continuous motion is the content, so it owns its own decay and repaints itself.
/// </para>
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    /// <summary>Bottom of the scale. Below this the bar reads as silence.</summary>
    private const double FloorDb = -60.0;

    /// <summary>Decay of the bar, in decibels per second. Broadcast meters sit near this.</summary>
    private const double DecayDbPerSecond = 60.0;

    /// <summary>How long the hold marker stays before it starts falling.</summary>
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1.2);

    private static readonly TimeSpan ClipTime = TimeSpan.FromSeconds(1.6);

    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.None, OnPeakChanged));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ClipBrushProperty = DependencyProperty.Register(
        nameof(ClipBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastUpdate;
    private TimeSpan _holdSetAt;
    private TimeSpan _clipUntil;
    private double _barDb = FloorDb;
    private double _holdDb = FloorDb;

    /// <summary>Linear peak from the engine, 0 to 1 and beyond when the signal clips.</summary>
    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush ClipBrush
    {
        get => (Brush)GetValue(ClipBrushProperty);
        set => SetValue(ClipBrushProperty, value);
    }

    /// <summary>False dims the whole meter, for a muted or stopped destination.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnPeakChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LevelMeter)d).Advance((double)e.NewValue);
    }

    private void Advance(double linearPeak)
    {
        TimeSpan now = _clock.Elapsed;
        double seconds = (now - _lastUpdate).TotalSeconds;
        _lastUpdate = now;
        if (seconds <= 0 || seconds > 0.5) seconds = 1.0 / 60.0;

        double incomingDb = linearPeak > 0.000001 ? 20.0 * Math.Log10(linearPeak) : FloorDb;

        // Attack is instant, release is timed. That asymmetry is what makes a meter readable.
        _barDb = incomingDb >= _barDb
            ? incomingDb
            : Math.Max(incomingDb, _barDb - DecayDbPerSecond * seconds);

        if (incomingDb >= _holdDb)
        {
            _holdDb = incomingDb;
            _holdSetAt = now;
        }
        else if (now - _holdSetAt > HoldTime)
        {
            _holdDb = Math.Max(incomingDb, _holdDb - DecayDbPerSecond * 0.4 * seconds);
        }

        if (linearPeak >= 0.999) _clipUntil = now + ClipTime;

        InvalidateVisual();
    }

    private static double Normalize(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0.0, 1.0);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        double radius = Math.Min(3.0, height / 2.0);
        var track = new Rect(0, 0, width, height);
        dc.DrawRoundedRectangle(TrackBrush, null, track, radius, radius);

        double opacity = IsActive ? 1.0 : 0.35;
        bool clipped = _clock.Elapsed < _clipUntil;

        double barWidth = Normalize(_barDb) * width;
        if (barWidth > 0.5)
        {
            dc.PushOpacity(opacity);
            dc.DrawRoundedRectangle(clipped ? ClipBrush : BarBrush, null,
                new Rect(0, 0, barWidth, height), radius, radius);
            dc.Pop();
        }

        double holdX = Normalize(_holdDb) * width;
        if (holdX > 1.0)
        {
            double markerWidth = 2.0;
            double x = Math.Min(holdX, width - markerWidth);
            dc.PushOpacity(opacity * 0.9);
            dc.DrawRectangle(clipped ? ClipBrush : BarBrush, null, new Rect(x, 0, markerWidth, height));
            dc.Pop();
        }
    }
}
