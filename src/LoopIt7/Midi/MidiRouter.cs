using NAudio.Midi;

namespace LoopIt7.Midi;

public sealed record MidiPort(int Index, string Name, string Manufacturer)
{
    public override string ToString() => Name;
}

/// <summary>
/// A MIDI patchbay: any input can be sent to any set of outputs. This is the half of Apple's
/// Audio MIDI Setup that Windows has never had a window for.
/// </summary>
public sealed class MidiRouter : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<int, MidiIn> _inputs = [];
    private readonly Dictionary<int, MidiOut> _outputs = [];
    private readonly Dictionary<int, int[]> _routes = [];
    private readonly Dictionary<int, int> _activity = [];

    private bool _disposed;

    public bool IsRunning { get; private set; }

    /// <summary>Raised off the UI thread when a port stops responding.</summary>
    public event EventHandler<string>? PortFailed;

    public static IReadOnlyList<MidiPort> GetInputs()
    {
        var ports = new List<MidiPort>();
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            try
            {
                var caps = MidiIn.DeviceInfo(i);
                ports.Add(new MidiPort(i, caps.ProductName, DescribeManufacturer(caps.Manufacturer)));
            }
            catch
            {
                // A port can vanish between the count and the query.
            }
        }

        return ports;
    }

    public static IReadOnlyList<MidiPort> GetOutputs()
    {
        var ports = new List<MidiPort>();
        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            try
            {
                var caps = MidiOut.DeviceInfo(i);
                ports.Add(new MidiPort(i, caps.ProductName, DescribeManufacturer(caps.Manufacturer)));
            }
            catch
            {
            }
        }

        return ports;
    }

    /// <summary>
    /// Applies the routing table and opens exactly the ports it needs. Safe to call while
    /// running: ports that stay in the table keep their handles and never drop a note.
    /// </summary>
    public void SetRoutes(IEnumerable<(int Input, int Output)> routes)
    {
        lock (_sync)
        {
            var table = routes
                .GroupBy(r => r.Input)
                .ToDictionary(g => g.Key, g => g.Select(r => r.Output).Distinct().ToArray());

            _routes.Clear();
            foreach (var pair in table) _routes[pair.Key] = pair.Value;

            if (IsRunning) Reconcile();
        }
    }

    public bool Start(out string? error)
    {
        lock (_sync)
        {
            IsRunning = true;
            error = null;

            try
            {
                Reconcile();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            return true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            IsRunning = false;
            CloseAll();
        }
    }

    /// <summary>
    /// Messages seen on this input since the last call. The interface turns it into a blink,
    /// which is the only way to tell a silent cable from a dead one.
    /// </summary>
    public int ReadActivity(int inputIndex)
    {
        lock (_sync)
        {
            if (!_activity.TryGetValue(inputIndex, out int count)) return 0;
            _activity[inputIndex] = 0;
            return count;
        }
    }

    private void Reconcile()
    {
        var wantedInputs = _routes.Keys.ToHashSet();
        var wantedOutputs = _routes.Values.SelectMany(v => v).ToHashSet();

        foreach (int index in _inputs.Keys.Where(i => !wantedInputs.Contains(i)).ToList())
        {
            CloseInput(index);
        }

        foreach (int index in _outputs.Keys.Where(o => !wantedOutputs.Contains(o)).ToList())
        {
            CloseOutput(index);
        }

        foreach (int index in wantedOutputs.Where(o => !_outputs.ContainsKey(o)))
        {
            try
            {
                _outputs[index] = new MidiOut(index);
            }
            catch (Exception ex)
            {
                PortFailed?.Invoke(this, $"MIDI output {index}: {ex.Message}");
            }
        }

        foreach (int index in wantedInputs.Where(i => !_inputs.ContainsKey(i)))
        {
            try
            {
                var input = new MidiIn(index);
                input.MessageReceived += OnMessage;
                input.ErrorReceived += OnMessage;
                input.Start();
                _inputs[index] = input;
                _activity[index] = 0;
            }
            catch (Exception ex)
            {
                PortFailed?.Invoke(this, $"MIDI input {index}: {ex.Message}");
            }
        }
    }

    private void OnMessage(object? sender, MidiInMessageEventArgs e)
    {
        if (sender is not MidiIn input) return;

        lock (_sync)
        {
            int index = _inputs.FirstOrDefault(p => ReferenceEquals(p.Value, input)).Key;
            if (!_routes.TryGetValue(index, out int[]? targets)) return;

            _activity[index] = _activity.GetValueOrDefault(index) + 1;

            foreach (int target in targets)
            {
                if (!_outputs.TryGetValue(target, out var output)) continue;

                try
                {
                    output.Send(e.RawMessage);
                }
                catch
                {
                    // A port pulled mid stream should not take the router down with it.
                }
            }
        }
    }

    private void CloseInput(int index)
    {
        if (!_inputs.Remove(index, out var input)) return;

        input.MessageReceived -= OnMessage;
        input.ErrorReceived -= OnMessage;
        try { input.Stop(); } catch { }
        try { input.Dispose(); } catch { }
        _activity.Remove(index);
    }

    private void CloseOutput(int index)
    {
        if (!_outputs.Remove(index, out var output)) return;

        try { output.Reset(); } catch { }
        try { output.Dispose(); } catch { }
    }

    private void CloseAll()
    {
        foreach (int index in _inputs.Keys.ToList()) CloseInput(index);
        foreach (int index in _outputs.Keys.ToList()) CloseOutput(index);
    }

    /// <summary>NAudio already names the manufacturer ids, so the enum name is the label.</summary>
    private static string DescribeManufacturer(object manufacturer) => manufacturer.ToString() ?? "MIDI";

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            IsRunning = false;
            CloseAll();
        }
    }
}
