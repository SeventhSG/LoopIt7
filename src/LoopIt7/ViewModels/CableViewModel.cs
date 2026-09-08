using System.Windows.Media;
using LoopIt7.Audio.Graph;

namespace LoopIt7.ViewModels;

/// <summary>
/// A patch cable. It watches both boxes it is tied to and redraws itself when either moves,
/// which is what makes dragging a node feel like moving real hardware on a desk.
/// </summary>
public sealed class CableViewModel : ObservableObject, IDisposable
{
    private readonly PathFigure _figure = new();
    private readonly BezierSegment _curve = new() { IsStroked = true };

    private double _gainDb;
    private bool _muted;
    private int _delayMs;
    private double _peak;
    private bool _isSelected;

    public CableViewModel(string id, PatchNodeViewModel source, PatchNodeViewModel destination)
    {
        Id = id;
        Source = source;
        Destination = destination;

        _figure.Segments.Add(_curve);
        Geometry = new PathGeometry([_figure]) { FillRule = FillRule.Nonzero };

        source.Moved += OnEndpointMoved;
        destination.Moved += OnEndpointMoved;

        // A cable is only audible while both ends are open, so it has to hear about that.
        source.PropertyChanged += OnEndpointStateChanged;
        destination.PropertyChanged += OnEndpointStateChanged;

        RemoveCommand = new RelayCommand(_ => RemoveRequested?.Invoke(this, EventArgs.Empty));
        ResetGainCommand = new RelayCommand(_ => GainDb = 0);
        ResetDelayCommand = new RelayCommand(_ => DelayMs = 0);

        Recompute();
    }

    public string Id { get; }

    /// <summary>
    /// Typed as the base box rather than as a source and a destination, because a virtual
    /// output is both and can sit at either end of a cable.
    /// </summary>
    public PatchNodeViewModel Source { get; }

    public PatchNodeViewModel Destination { get; }

    public PathGeometry Geometry { get; }

    public RelayCommand RemoveCommand { get; }
    public RelayCommand ResetGainCommand { get; }
    public RelayCommand ResetDelayCommand { get; }

    public event EventHandler? RemoveRequested;
    public event EventHandler? MixChanged;

    public string Description => $"{Source.Title} to {Destination.Title}";

    public double GainDb
    {
        get => _gainDb;
        set
        {
            if (!SetProperty(ref _gainDb, Math.Round(value, 1))) return;
            OnPropertyChanged(nameof(GainText));
            OnPropertyChanged(nameof(IsTrimmed));
            MixChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string GainText => _gainDb <= MixMath.MinDb ? "-inf" : $"{_gainDb:+0.0;-0.0;0.0} dB";

    public float LinearGain => MixMath.DbToLinear(_gainDb);

    /// <summary>True when this cable is doing something other than passing the signal through.</summary>
    public bool IsTrimmed => Math.Abs(_gainDb) > 0.05 || _muted || _delayMs > 0;

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetProperty(ref _muted, value)) return;
            OnPropertyChanged(nameof(IsAudible));
            OnPropertyChanged(nameof(IsTrimmed));
            MixChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int DelayMs
    {
        get => _delayMs;
        set
        {
            int clamped = Math.Clamp(value, 0, DelaySampleProviderLimits.MaxDelayMilliseconds);
            if (!SetProperty(ref _delayMs, clamped)) return;
            OnPropertyChanged(nameof(DelayText));
            OnPropertyChanged(nameof(IsTrimmed));
            MixChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string DelayText => $"{_delayMs} ms";

    public double Peak
    {
        get => _peak;
        set
        {
            if (!SetProperty(ref _peak, value)) return;
            OnPropertyChanged(nameof(SignalOpacity));
        }
    }

    /// <summary>
    /// How brightly to draw the cable. Tied to what it is actually carrying, on the same
    /// decibel scale as the meters, so a glance across the canvas shows where signal is.
    /// </summary>
    public double SignalOpacity
    {
        get
        {
            if (!IsAudible) return 0.5;

            double db = _peak > 0.000001 ? 20.0 * Math.Log10(_peak) : -60.0;
            double normalized = Math.Clamp((db + 60.0) / 60.0, 0, 1);
            return 0.34 + 0.66 * normalized;
        }
    }

    public bool IsAudible => !_muted && Source.IsLive && Destination.IsLive;

    private void OnEndpointStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(PatchNodeViewModel.IsLive)) return;
        OnPropertyChanged(nameof(IsAudible));
        OnPropertyChanged(nameof(SignalOpacity));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Midpoint of the curve, where the inline cable controls sit when selected.</summary>
    public double MidX { get; private set; }

    public double MidY { get; private set; }

    /// <summary>Top left of the cable inspector, centred under the midpoint.</summary>
    public double InspectorX => MidX - 126;

    public double InspectorY => MidY + 18;

    private void OnEndpointMoved(object? sender, EventArgs e) => Recompute();

    private void Recompute()
    {
        double x1 = Source.OutputPortX;
        double y1 = Source.PortY;
        double x2 = Destination.InputPortX;
        double y2 = Destination.PortY;

        // A fixed horizontal pull would collapse when boxes sit close together and look limp
        // when they are far apart. Scaling with the gap keeps the curve reading as a cable.
        double reach = Math.Clamp(Math.Abs(x2 - x1) * 0.5, 60, 190);

        _figure.StartPoint = new Point(x1, y1);
        _curve.Point1 = new Point(x1 + reach, y1);
        _curve.Point2 = new Point(x2 - reach, y2);
        _curve.Point3 = new Point(x2, y2);

        MidX = (x1 + x2) / 2;
        MidY = (y1 + y2) / 2;

        OnPropertyChanged(nameof(MidX));
        OnPropertyChanged(nameof(MidY));
        OnPropertyChanged(nameof(InspectorX));
        OnPropertyChanged(nameof(InspectorY));
    }

    public void Dispose()
    {
        Source.Moved -= OnEndpointMoved;
        Destination.Moved -= OnEndpointMoved;
        Source.PropertyChanged -= OnEndpointStateChanged;
        Destination.PropertyChanged -= OnEndpointStateChanged;
    }
}

/// <summary>Mirrors the engine's delay limit so the view model layer does not reach into it.</summary>
public static class DelaySampleProviderLimits
{
    public const int MaxDelayMilliseconds = LoopIt7.Audio.DelaySampleProvider.MaxDelayMilliseconds;
}
