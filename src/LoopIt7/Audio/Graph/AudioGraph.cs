using NAudio.Wave;

namespace LoopIt7.Audio.Graph;

/// <summary>
/// The patchbay. Sources, destinations and the cables between them, with the wiring rules
/// that keep the audio threads consistent while the user rearranges things underneath.
/// <para>
/// One source can feed many destinations and one destination can sum many sources. Every
/// cable owns its own buffer, so the graph never needs a global clock.
/// </para>
/// </summary>
public sealed class AudioGraph : IDisposable
{
    private readonly DeviceService _devices;
    private readonly object _sync = new();
    private readonly Dictionary<string, SourceNode> _sources = [];
    private readonly Dictionary<string, DestinationNode> _destinations = [];
    private readonly Dictionary<string, Connection> _connections = [];

    private bool _disposed;

    public AudioGraph(DeviceService devices)
    {
        _devices = devices;
    }

    public bool IsRunning { get; private set; }

    /// <summary>WASAPI buffer per endpoint, in milliseconds. Applied on the next start.</summary>
    public int BufferMilliseconds { get; set; } = 10;

    /// <summary>Raised off the UI thread when a node fails on its own and wants a retry.</summary>
    public event EventHandler<GraphErrorEventArgs>? NodeFailed;

    // Building the graph

    public void AddDeviceSource(string id, string displayName, string deviceId, bool loopback)
    {
        lock (_sync)
        {
            RemoveNodeCore(id);
            var node = new DeviceSourceNode(id, displayName, deviceId, loopback, _devices)
            {
                BufferMilliseconds = BufferMilliseconds,
                TargetQueueMilliseconds = TargetQueue
            };

            node.Failed += OnNodeFailed;
            _sources[id] = node;
            if (IsRunning) StartSource(node);
        }
    }

    public void AddApplicationSource(string id, string displayName, int processId, string executableName)
    {
        lock (_sync)
        {
            RemoveNodeCore(id);
            var node = new AppSourceNode(id, displayName, processId, executableName)
            {
                TargetQueueMilliseconds = TargetQueue
            };

            node.Failed += OnNodeFailed;
            _sources[id] = node;
            if (IsRunning) StartSource(node);
        }
    }

    public void AddDestination(string id, string displayName, string deviceId)
    {
        lock (_sync)
        {
            RemoveNodeCore(id);
            var node = new DestinationNode(id, displayName, deviceId, _devices)
            {
                BufferMilliseconds = BufferMilliseconds
            };

            node.Failed += OnNodeFailed;
            _destinations[id] = node;

            if (IsRunning)
            {
                RewireDestination(node);
                node.Start(out _);
            }
        }
    }

    public void RemoveNode(string nodeId)
    {
        lock (_sync)
        {
            RemoveNodeCore(nodeId);
        }
    }

    public bool Connect(string connectionId, string sourceId, string destinationId)
    {
        lock (_sync)
        {
            if (!_sources.TryGetValue(sourceId, out var source)) return false;
            if (!_destinations.TryGetValue(destinationId, out var destination)) return false;

            // One cable per pair. Patching the same pair twice would just double its level.
            if (_connections.Values.Any(c => c.SourceId == sourceId && c.DestinationId == destinationId)) return false;

            _connections[connectionId] = new Connection(connectionId, sourceId, destinationId, source.StereoFormat);

            RewireSource(source);
            RewireDestination(destination);
            return true;
        }
    }

    public void Disconnect(string connectionId)
    {
        lock (_sync)
        {
            if (!_connections.Remove(connectionId, out var connection)) return;

            if (_sources.TryGetValue(connection.SourceId, out var source)) RewireSource(source);
            if (_destinations.TryGetValue(connection.DestinationId, out var destination)) RewireDestination(destination);

            connection.Dispose();
        }
    }

    // Running

    public bool Start(out string? error)
    {
        lock (_sync)
        {
            StopCore();

            if (_sources.Count == 0)
            {
                error = "Add a source first.";
                return false;
            }

            if (_destinations.Count == 0)
            {
                error = "Add an output first.";
                return false;
            }

            foreach (var source in _sources.Values)
            {
                if (source is DeviceSourceNode device) device.BufferMilliseconds = BufferMilliseconds;
                source.TargetQueueMilliseconds = TargetQueue;
                source.Start(out _);
            }

            // Cables were built against whatever format the source last reported. Now that
            // the captures are open, the real rates are known.
            foreach (var connection in _connections.Values)
            {
                if (_sources.TryGetValue(connection.SourceId, out var source))
                {
                    connection.Reformat(source.StereoFormat);
                }
            }

            foreach (var source in _sources.Values) RewireSource(source);

            foreach (var destination in _destinations.Values)
            {
                destination.BufferMilliseconds = BufferMilliseconds;
                RewireDestination(destination);
                destination.Start(out _);
            }

            IsRunning = true;
            error = null;
            return true;
        }
    }

    public void Stop()
    {
        lock (_sync) StopCore();
    }

    /// <summary>
    /// Reopens everything that is waiting or failed. Called after Windows reports a device
    /// change and on a slow timer, so an unplugged headset or a restarted app comes back on
    /// its own instead of needing the whole graph restarted.
    /// </summary>
    public bool RetryPending()
    {
        lock (_sync)
        {
            if (!IsRunning) return false;

            bool changed = false;

            foreach (var source in _sources.Values)
            {
                if (source.Status == NodeStatus.Live) continue;
                if (!StartSource(source)) continue;
                changed = true;
            }

            foreach (var destination in _destinations.Values)
            {
                if (destination.Status == NodeStatus.Live) continue;
                RewireDestination(destination);
                if (destination.Start(out _)) changed = true;
            }

            return changed;
        }
    }

    // Live control

    public void ConfigureSource(string id, float linearGain, bool muted, float pan = 0f)
    {
        lock (_sync)
        {
            if (!_sources.TryGetValue(id, out var source)) return;
            source.LinearGain = linearGain;
            source.Muted = muted;
            source.Pan = pan;
        }
    }

    public void ConfigureDestination(string id, float linearGain, bool muted, float pan = 0f)
    {
        lock (_sync)
        {
            if (!_destinations.TryGetValue(id, out var destination)) return;
            destination.LinearGain = linearGain;
            destination.Muted = muted;
            destination.Pan = pan;
        }
    }

    public void ConfigureConnection(string id, float linearGain, bool muted, int delayMs)
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(id, out var connection)) return;
            connection.LinearGain = linearGain;
            connection.Muted = muted;
            connection.DelayMilliseconds = delayMs;
        }
    }

    // Reading back

    public NodeStatus GetSourceStatus(string id, out string? detail, out int sampleRate, out int channels)
    {
        lock (_sync)
        {
            if (_sources.TryGetValue(id, out var source))
            {
                detail = source.StatusDetail;
                sampleRate = source.Status == NodeStatus.Live ? source.StereoFormat.SampleRate : 0;
                channels = source.NativeChannels;
                return source.Status;
            }
        }

        detail = null;
        sampleRate = 0;
        channels = 0;
        return NodeStatus.Idle;
    }

    public NodeStatus GetDestinationStatus(string id, out string? detail, out int sampleRate, out int channels)
    {
        lock (_sync)
        {
            if (_destinations.TryGetValue(id, out var destination))
            {
                detail = destination.StatusDetail;
                sampleRate = destination.Status == NodeStatus.Live ? destination.SampleRate : 0;
                channels = destination.Channels;
                return destination.Status;
            }
        }

        detail = null;
        sampleRate = 0;
        channels = 0;
        return NodeStatus.Idle;
    }

    public float ReadSourcePeak(string id)
    {
        lock (_sync) return _sources.TryGetValue(id, out var source) ? source.ReadPeak() : 0f;
    }

    public float ReadDestinationPeak(string id)
    {
        lock (_sync) return _destinations.TryGetValue(id, out var destination) ? destination.ReadPeak() : 0f;
    }

    public float ReadConnectionPeak(string id)
    {
        lock (_sync) return _connections.TryGetValue(id, out var connection) ? connection.ReadPeak() : 0f;
    }

    /// <summary>Process ids move when a program restarts. This reports the one in use now.</summary>
    public int GetApplicationProcessId(string id)
    {
        lock (_sync) return _sources.TryGetValue(id, out var source) && source is AppSourceNode app ? app.ProcessId : 0;
    }

    // Internals

    /// <summary>Headroom the cables are allowed to build up, scaled off the buffer setting.</summary>
    private int TargetQueue => Math.Max(20, BufferMilliseconds * 3);

    private bool StartSource(SourceNode source)
    {
        if (source is DeviceSourceNode device) device.BufferMilliseconds = BufferMilliseconds;
        source.TargetQueueMilliseconds = TargetQueue;

        if (!source.Start(out _)) return false;

        // A reopened source may have landed on a different sample rate, which invalidates the
        // buffers of every cable leaving it and the mix of every destination they reach.
        foreach (var connection in ConnectionsFrom(source.Id))
        {
            connection.Reformat(source.StereoFormat);
        }

        RewireSource(source);

        foreach (var destinationId in ConnectionsFrom(source.Id).Select(c => c.DestinationId).Distinct())
        {
            if (_destinations.TryGetValue(destinationId, out var destination)) RewireDestination(destination);
        }

        return true;
    }

    private IEnumerable<Connection> ConnectionsFrom(string sourceId) =>
        _connections.Values.Where(c => c.SourceId == sourceId).ToList();

    private void RewireSource(SourceNode source) =>
        source.SetConnections(_connections.Values.Where(c => c.SourceId == source.Id));

    private void RewireDestination(DestinationNode destination) =>
        destination.SetConnections(_connections.Values.Where(c => c.DestinationId == destination.Id));

    private void RemoveNodeCore(string nodeId)
    {
        foreach (var connection in _connections.Values
                     .Where(c => c.SourceId == nodeId || c.DestinationId == nodeId)
                     .Select(c => c.Id)
                     .ToList())
        {
            if (!_connections.Remove(connection, out var removed)) continue;

            if (_sources.TryGetValue(removed.SourceId, out var peerSource)) RewireSource(peerSource);
            if (_destinations.TryGetValue(removed.DestinationId, out var peerDestination)) RewireDestination(peerDestination);
            removed.Dispose();
        }

        if (_sources.Remove(nodeId, out var source))
        {
            source.Failed -= OnNodeFailed;
            source.Dispose();
        }

        if (_destinations.Remove(nodeId, out var destination))
        {
            destination.Failed -= OnNodeFailed;
            destination.Dispose();
        }
    }

    private void OnNodeFailed(object? sender, GraphErrorEventArgs e) => NodeFailed?.Invoke(this, e);

    private void StopCore()
    {
        IsRunning = false;
        foreach (var destination in _destinations.Values) destination.Stop();
        foreach (var source in _sources.Values) source.Stop();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;

            StopCore();

            foreach (var source in _sources.Values)
            {
                source.Failed -= OnNodeFailed;
                source.Dispose();
            }

            foreach (var destination in _destinations.Values)
            {
                destination.Failed -= OnNodeFailed;
                destination.Dispose();
            }

            foreach (var connection in _connections.Values) connection.Dispose();

            _sources.Clear();
            _destinations.Clear();
            _connections.Clear();
        }
    }
}
