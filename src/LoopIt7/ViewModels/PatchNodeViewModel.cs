using LoopIt7.Audio;
using LoopIt7.Audio.Graph;

namespace LoopIt7.ViewModels;

/// <summary>
/// A box on the patchbay. Position lives here because the canvas is the model: where a user
/// puts a node is part of their setup and gets saved with it.
/// </summary>
public abstract class PatchNodeViewModel : ObservableObject
{
    /// <summary>Fixed so cable endpoints can be computed without measuring the visual tree.</summary>
    public const double NodeWidth = 268;

    public const double NodeHeight = 132;

    /// <summary>Vertical centre of the card, where the ports sit.</summary>
    public const double PortOffsetY = NodeHeight / 2;

    private double _x;
    private double _y;
    private double _gainDb;
    private bool _muted;
    private double _peak;
    private bool _isSelected;
    private NodeStatus _status = NodeStatus.Idle;
    private string _statusText = "Not routing";
    private string _formatText = string.Empty;
    private string _title;
    private string _subtitle;

    protected PatchNodeViewModel(string id, string title, string subtitle)
    {
        Id = id;
        _title = title;
        _subtitle = subtitle;

        RemoveCommand = new RelayCommand(_ => RemoveRequested?.Invoke(this, EventArgs.Empty));
        ResetGainCommand = new RelayCommand(_ => GainDb = 0);
        ResetPanCommand = new RelayCommand(_ => Pan = 0);
    }

    public string Id { get; }

    /// <summary>Segoe Fluent glyph identifying what kind of thing this node is.</summary>
    public abstract string Glyph { get; }

    /// <summary>True when the engine drives this box as a source of audio.</summary>
    public abstract bool IsSource { get; }

    /// <summary>True when cables can start here, which puts a port on the right hand edge.</summary>
    public virtual bool CanSend => IsSource;

    /// <summary>True when cables can end here, which puts a port on the left hand edge.</summary>
    public virtual bool CanReceive => !IsSource;

    /// <summary>True for a virtual output, which is the only box that can adopt a program.</summary>
    public virtual bool CanAssignApps => false;

    /// <summary>
    /// Whether this box can be given a way in from the rest of Windows. Only a virtual output
    /// can: everything else on the canvas is already a device, or is one program.
    /// </summary>
    public virtual bool CanPublish => false;

    /// <summary>
    /// True for a running program, the only thing that can be taken off its own output.
    /// <para>
    /// Declared here rather than only on the source, because the card template is shared by
    /// every box and a binding that cannot be resolved leaves the control visible.
    /// </para>
    /// </summary>
    public virtual bool SupportsExclusive => false;

    /// <summary>Bound by the card for the box above. Meaningless anywhere else.</summary>
    public virtual bool Exclusive
    {
        get => false;
        set { }
    }

    /// <summary>Extra line under the title. Only a program that has been taken over has one.</summary>
    public virtual string RouteText => string.Empty;

    public RelayCommand RemoveCommand { get; }
    public RelayCommand ResetGainCommand { get; }
    public RelayCommand ResetPanCommand { get; }

    public event EventHandler? RemoveRequested;

    /// <summary>Raised when the node moves, so the cables attached to it can redraw.</summary>
    public event EventHandler? Moved;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public double X
    {
        get => _x;
        set
        {
            if (!SetProperty(ref _x, value)) return;
            OnPropertyChanged(nameof(OutputPortX));
            OnPropertyChanged(nameof(InputPortX));
            Moved?.Invoke(this, EventArgs.Empty);
        }
    }

    public double Y
    {
        get => _y;
        set
        {
            if (!SetProperty(ref _y, value)) return;
            OnPropertyChanged(nameof(PortY));
            Moved?.Invoke(this, EventArgs.Empty);
        }
    }

    public double OutputPortX => _x + NodeWidth;
    public double InputPortX => _x;
    public double PortY => _y + PortOffsetY;

    public double GainDb
    {
        get => _gainDb;
        set
        {
            if (!SetProperty(ref _gainDb, Math.Round(value, 1))) return;
            OnPropertyChanged(nameof(GainText));
            MixChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string GainText => _gainDb <= MixMath.MinDb ? "-inf" : $"{_gainDb:+0.0;-0.0;0.0} dB";

    public float LinearGain => MixMath.DbToLinear(_gainDb);

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetProperty(ref _muted, value)) return;
            OnPropertyChanged(nameof(IsAudible));
            OnPropertyChanged(nameof(EffectiveMuted));
            MixChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private double _pan;

    /// <summary>Stereo balance, -1 hard left to +1 hard right.</summary>
    public double Pan
    {
        get => _pan;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 2), -1, 1);

            // A detent at the centre. Without it, dead centre is almost impossible to hit
            // with a mouse and every channel ends up a percent or two off.
            if (Math.Abs(clamped) < 0.04) clamped = 0;

            if (!SetProperty(ref _pan, clamped)) return;
            OnPropertyChanged(nameof(PanText));
            OnPropertyChanged(nameof(IsPanned));
            MixChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public float LinearPan => (float)_pan;

    public bool IsPanned => _pan != 0;

    public string PanText => _pan switch
    {
        0 => "C",
        < 0 => $"L{Math.Abs(_pan) * 100:0}",
        _ => $"R{_pan * 100:0}"
    };

    private bool _solo;

    /// <summary>
    /// Solo is a mixer wide state, not a per node one: the moment anything is soloed,
    /// everything that is not goes quiet. The main view model owns that arithmetic.
    /// </summary>
    public bool Solo
    {
        get => _solo;
        set
        {
            if (!SetProperty(ref _solo, value)) return;
            SoloChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Only sources can be soloed. An output is where you listen, not what you pick.</summary>
    public virtual bool SupportsSolo => IsSource;

    private bool _silencedBySolo;

    /// <summary>Set by the mixer when something else is soloed and this is not.</summary>
    public bool SilencedBySolo
    {
        get => _silencedBySolo;
        set
        {
            if (!SetProperty(ref _silencedBySolo, value)) return;
            OnPropertyChanged(nameof(IsAudible));
            OnPropertyChanged(nameof(EffectiveMuted));
        }
    }

    /// <summary>What the engine is actually told: muted by hand, or silenced by someone else's solo.</summary>
    public bool EffectiveMuted => _muted || _silencedBySolo;

    public event EventHandler? SoloChanged;

    public event EventHandler? MixChanged;

    public double Peak
    {
        get => _peak;
        set => SetProperty(ref _peak, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsAudible => !EffectiveMuted && _status == NodeStatus.Live;

    public bool IsLive => _status == NodeStatus.Live;

    public bool IsFaulted => _status is NodeStatus.Failed or NodeStatus.Waiting;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Rate and channel count once the node is open, so resampling is visible.</summary>
    public string FormatText
    {
        get => _formatText;
        private set => SetProperty(ref _formatText, value);
    }

    public void ApplyStatus(NodeStatus status, string? detail, int sampleRate, int channels)
    {
        if (SetProperty(ref _status, status, nameof(IsLive)))
        {
            OnPropertyChanged(nameof(IsAudible));
            OnPropertyChanged(nameof(IsFaulted));
        }

        StatusText = status switch
        {
            NodeStatus.Live => "Live",
            NodeStatus.Waiting => detail ?? "Waiting",
            NodeStatus.Failed => detail ?? "Could not open",
            _ => "Not routing"
        };

        FormatText = status == NodeStatus.Live && sampleRate > 0
            ? $"{sampleRate / 1000.0:0.#} kHz · {DescribeChannels(channels)}"
            : string.Empty;
    }

    private static string DescribeChannels(int channels) => channels switch
    {
        <= 0 => "stereo",
        1 => "mono",
        2 => "stereo",
        _ => $"{channels} ch"
    };
}

/// <summary>A microphone, a line input, a device loopback, or a running application.</summary>
public sealed class SourceNodeViewModel : PatchNodeViewModel
{
    public SourceNodeViewModel(string id, string title, string subtitle, SourceKind kind, string deviceId, int processId, string executableName)
        : base(id, title, subtitle)
    {
        Kind = kind;
        DeviceId = deviceId;
        ProcessId = processId;
        ExecutableName = executableName;
    }

    public SourceKind Kind { get; }
    public string DeviceId { get; }
    public int ProcessId { get; set; }
    public string ExecutableName { get; }

    public override bool IsSource => true;

    /// <summary>Only a program can be taken off its own output. A microphone has none.</summary>
    public override bool SupportsExclusive => Kind == SourceKind.Application;

    private bool _exclusive;

    /// <summary>
    /// Mute this program in the Windows volume mixer while routing runs, so it is heard only
    /// where LoopIt7 sends it instead of twice. Cleared again the moment routing stops, the
    /// box is removed, or the app closes.
    /// </summary>
    public override bool Exclusive
    {
        get => _exclusive;
        set
        {
            if (!SetProperty(ref _exclusive, value)) return;
            OnPropertyChanged(nameof(RouteText));
            ExclusiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ExclusiveChanged;

    /// <summary>Reads out under the title, so the takeover is visible without hunting for it.</summary>
    public override string RouteText => _exclusive ? "only through LoopIt7" : string.Empty;

    public override string Glyph => Kind switch
    {
        SourceKind.Device => "\uE720",           // microphone
        SourceKind.DeviceLoopback => "\uE767",   // a speaker, tapped on the way out
        _ => "\uECAA"                            // application
    };
}

/// <summary>
/// A virtual output. It is a source and a destination at the same time: cables end at it,
/// it sums them, and the sum leaves down cables of its own.
/// <para>
/// The engine treats it as a source, so it carries a source's fader and meter. What it does
/// not carry is solo, because solo means "only this one of the things I picked", and nobody
/// picks a bus.
/// </para>
/// </summary>
public sealed class VirtualOutputNodeViewModel : PatchNodeViewModel
{
    public VirtualOutputNodeViewModel(string id, string title)
        : base(id, title, "virtual output")
    {
    }

    /// <summary>
    /// The live list of programs Windows says are playing, shared with the rest of the app.
    /// It hangs off the node because the picker that uses it lives inside the node's card,
    /// inside a popup, where the main view model is out of reach.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<Audio.AudioApplication>? Applications { get; set; }

    private string? _inletHint;

    /// <summary>
    /// What to choose in another program to reach this box, once it has been given a way in
    /// from the rest of Windows. Null while the box is reachable only from inside LoopIt7,
    /// which is how every virtual output starts.
    /// </summary>
    public string? InletHint
    {
        get => _inletHint;
        set
        {
            if (SetProperty(ref _inletHint, value)) OnPropertyChanged(nameof(HasInletHint));
        }
    }

    public bool HasInletHint => !string.IsNullOrEmpty(_inletHint);

    private string? _inletBadge;

    /// <summary>
    /// The short form of the same thing, for the line under the title. "via System" fits on a
    /// card; the full sentence is the tooltip.
    /// </summary>
    public string? InletBadge
    {
        get => _inletBadge;
        set => SetProperty(ref _inletBadge, value);
    }

    /// <summary>
    /// The cables that could give this box a way in from Windows, shared with the rest of the
    /// app. It hangs off the node for the same reason the application list does: the picker
    /// lives inside the node's card, inside a popup, out of the main view model's reach.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<Audio.CableInlet>? Inlets { get; set; }

    /// <summary>
    /// The cable playback end this box was given, or empty when it has none. Saved with the
    /// patch so the choice survives a restart.
    /// </summary>
    public string InletFeedDeviceId { get; set; } = string.Empty;

    public override bool IsSource => true;

    public override bool CanReceive => true;

    public override bool CanAssignApps => true;

    public override bool CanPublish => true;

    public override bool SupportsSolo => false;

    public override string Glyph => "\uE8AB";   // two arrows: what arrives here leaves again
}

/// <summary>A playback endpoint: speakers, headphones, an interface, or a virtual cable.</summary>
public sealed class DestinationNodeViewModel : PatchNodeViewModel
{
    public DestinationNodeViewModel(string id, string title, string subtitle, string deviceId, bool isVirtualCable)
        : base(id, title, subtitle)
    {
        DeviceId = deviceId;
        IsVirtualCable = isVirtualCable;
    }

    public string DeviceId { get; }

    /// <summary>
    /// True when this endpoint is a software cable. Worth showing: sending audio there means
    /// handing it to another program rather than to a speaker.
    /// </summary>
    public bool IsVirtualCable { get; }

    private string? _pickupHint;

    /// <summary>
    /// For a cable destination, what the other program should be told to listen to. Null for
    /// real hardware, where the answer is "your ears".
    /// </summary>
    public string? PickupHint
    {
        get => _pickupHint;
        set
        {
            if (SetProperty(ref _pickupHint, value)) OnPropertyChanged(nameof(HasPickupHint));
        }
    }

    public bool HasPickupHint => !string.IsNullOrEmpty(_pickupHint);

    public override bool IsSource => false;

    public override string Glyph => IsVirtualCable ? "\uE71B" : "\uE767";
}

public static class MixMath
{
    /// <summary>Bottom of every fader in the app. At this position the signal is silent.</summary>
    public const double MinDb = -60.0;

    public const double MaxDb = 12.0;

    public static float DbToLinear(double db) => db <= MinDb ? 0f : (float)Math.Pow(10.0, db / 20.0);
}
