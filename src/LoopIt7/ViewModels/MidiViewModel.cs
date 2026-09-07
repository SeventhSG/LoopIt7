using System.Collections.ObjectModel;
using LoopIt7.Midi;

namespace LoopIt7.ViewModels;

/// <summary>One cell in the MIDI matrix: this input sent to that output, or not.</summary>
public sealed class MidiRouteCell : ObservableObject
{
    private readonly Action _changed;
    private bool _isRouted;

    public MidiRouteCell(int inputIndex, int outputIndex, string outputName, Action changed)
    {
        InputIndex = inputIndex;
        OutputIndex = outputIndex;
        OutputName = outputName;
        _changed = changed;
    }

    public int InputIndex { get; }
    public int OutputIndex { get; }
    public string OutputName { get; }

    public bool IsRouted
    {
        get => _isRouted;
        set
        {
            if (SetProperty(ref _isRouted, value)) _changed();
        }
    }
}

public sealed class MidiInputRow : ObservableObject
{
    private double _activity;

    public MidiInputRow(MidiPort port, IEnumerable<MidiRouteCell> cells)
    {
        Port = port;
        Cells = new ObservableCollection<MidiRouteCell>(cells);
    }

    public MidiPort Port { get; }
    public ObservableCollection<MidiRouteCell> Cells { get; }

    public string Name => Port.Name;
    public string Manufacturer => Port.Manufacturer;

    /// <summary>Decaying 0 to 1 blink driven by how many messages arrived in the last tick.</summary>
    public double Activity
    {
        get => _activity;
        set => SetProperty(ref _activity, value);
    }

    public bool IsConnected => Cells.Any(c => c.IsRouted);

    public void NotifyConnectionChanged() => OnPropertyChanged(nameof(IsConnected));
}

/// <summary>
/// The MIDI half of the app. Routing here is live the moment a cell is ticked: MIDI thru
/// costs nothing to leave running, and tying it to the audio transport would only surprise
/// someone who came for a keyboard and a sound module.
/// </summary>
public sealed class MidiViewModel : ObservableObject, IDisposable
{
    private readonly MidiRouter _router = new();
    private readonly Action _persist;

    private string? _message;
    private bool _loading;

    public MidiViewModel(Action persist)
    {
        _persist = persist;
        RefreshCommand = new RelayCommand(_ => Refresh());
        _router.PortFailed += (_, text) => Message = text;
        Refresh();
    }

    public ObservableCollection<MidiInputRow> Inputs { get; } = [];
    public ObservableCollection<MidiPort> Outputs { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public bool HasPorts => Inputs.Count > 0 && Outputs.Count > 0;

    public bool HasInputs => Inputs.Count > 0;
    public bool HasOutputs => Outputs.Count > 0;

    /// <summary>
    /// What was actually found. "No MIDI ports" and "one output but nothing to send to it"
    /// are different problems and want different next steps.
    /// </summary>
    public string PortSummary
    {
        get
        {
            string inputs = Inputs.Count == 1 ? "1 input" : $"{Inputs.Count} inputs";
            string outputs = Outputs.Count == 1 ? "1 output" : $"{Outputs.Count} outputs";
            return $"Windows reports {inputs} and {outputs}.";
        }
    }

    /// <summary>Names of the outputs found, so an empty matrix still tells the user something.</summary>
    public string OutputSummary => Outputs.Count == 0
        ? string.Empty
        : "Outputs found: " + string.Join(", ", Outputs.Select(o => o.Name));

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    /// <summary>Rebuilds the port lists, keeping any routes whose ports are still present.</summary>
    public void Refresh()
    {
        var previous = CurrentRoutes().ToHashSet();

        _loading = true;
        Inputs.Clear();
        Outputs.Clear();

        var outputs = MidiRouter.GetOutputs();
        foreach (var output in outputs) Outputs.Add(output);

        foreach (var input in MidiRouter.GetInputs())
        {
            var cells = outputs.Select(o => new MidiRouteCell(input.Index, o.Index, o.Name, OnRoutesChanged)).ToList();
            foreach (var cell in cells)
            {
                cell.IsRouted = previous.Contains((cell.InputIndex, cell.OutputIndex));
            }

            Inputs.Add(new MidiInputRow(input, cells));
        }

        _loading = false;

        OnPropertyChanged(nameof(HasPorts));
        OnPropertyChanged(nameof(HasInputs));
        OnPropertyChanged(nameof(HasOutputs));
        OnPropertyChanged(nameof(PortSummary));
        OnPropertyChanged(nameof(OutputSummary));
        OnRoutesChanged();
    }

    public IEnumerable<(int Input, int Output)> CurrentRoutes() =>
        Inputs.SelectMany(row => row.Cells).Where(c => c.IsRouted).Select(c => (c.InputIndex, c.OutputIndex)).ToList();

    /// <summary>Restores saved routes by port name, because indices move when devices change.</summary>
    public void ApplySavedRoutes(IEnumerable<(string Input, string Output)> routes)
    {
        _loading = true;

        foreach (var (inputName, outputName) in routes)
        {
            var row = Inputs.FirstOrDefault(r => string.Equals(r.Name, inputName, StringComparison.OrdinalIgnoreCase));
            var cell = row?.Cells.FirstOrDefault(c => string.Equals(c.OutputName, outputName, StringComparison.OrdinalIgnoreCase));
            if (cell is not null) cell.IsRouted = true;
        }

        _loading = false;
        OnRoutesChanged();
    }

    public IEnumerable<(string Input, string Output)> NamedRoutes() =>
        Inputs.SelectMany(row => row.Cells.Where(c => c.IsRouted).Select(c => (row.Name, c.OutputName))).ToList();

    /// <summary>Called on the meter tick so the activity lamps decay smoothly.</summary>
    public void Tick()
    {
        foreach (var row in Inputs)
        {
            int messages = _router.ReadActivity(row.Port.Index);
            double level = messages > 0 ? 1.0 : Math.Max(0, row.Activity - 0.12);
            row.Activity = level;
        }
    }

    private void OnRoutesChanged()
    {
        if (_loading) return;

        var routes = CurrentRoutes().ToList();
        _router.SetRoutes(routes);

        if (routes.Count > 0 && !_router.IsRunning)
        {
            if (!_router.Start(out string? error) && error is not null) Message = error;
        }
        else if (routes.Count == 0 && _router.IsRunning)
        {
            _router.Stop();
        }

        foreach (var row in Inputs) row.NotifyConnectionChanged();
        _persist();
    }

    public void Dispose() => _router.Dispose();
}
