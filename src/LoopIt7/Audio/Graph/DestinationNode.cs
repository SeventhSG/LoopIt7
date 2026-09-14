using LoopIt7.Audio.Interop;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LoopIt7.Audio.Graph;

/// <summary>
/// A playback endpoint. It sums every cable arriving at it and hands the result to WASAPI
/// on that endpoint's own clock, so one slow device can never hold up the others.
/// </summary>
internal sealed class DestinationNode : IDisposable
{
    private readonly DeviceService _devices;
    private readonly object _sync = new();
    private readonly List<Connection> _connections = [];

    private WasapiOut? _player;
    private LowLatencyRender? _fast;

    /// <summary>
    /// Set once this device has shown it does not wake an exclusive stream every period, so
    /// every later start opens it on a timer straight away.
    /// </summary>
    private volatile bool _exclusiveTimed;
    private MMDevice? _device;
    private MixSampleProvider? _mixer;
    private SmoothGainSampleProvider? _gain;
    private RunawayGuardSampleProvider? _guard;

    public DestinationNode(string id, string displayName, string deviceId, DeviceService devices)
    {
        Id = id;
        DisplayName = displayName;
        DeviceId = deviceId;
        _devices = devices;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string DeviceId { get; }

    public NodeStatus Status { get; private set; } = NodeStatus.Idle;
    public string? StatusDetail { get; private set; }
    public string? StatusHint { get; private set; }

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    /// <summary>The engine period the output actually runs at, in milliseconds.</summary>
    public double PeriodMilliseconds { get; private set; }

    /// <summary>Hold the device exclusively at its smallest period. See the input's twin.</summary>
    public bool LowLatencyMode { get; set; }

    /// <summary>What the output got: "exclusive · 144 samples (3.0 ms)", or the shared period.</summary>
    public string LatencyText { get; private set; } = string.Empty;

    public int BufferMilliseconds { get; set; } = 10;

    private float _linearGain = 1f;
    public float LinearGain
    {
        get => _linearGain;
        set { _linearGain = value; ApplyGain(); }
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            bool unmuting = _muted && !value;
            _muted = value;
            ApplyGain();

            // The runaway guard holds an output silent until the user unmutes it, and this is
            // that moment. Only on the change: a fader move carries the same unmuted state and
            // must not let a loop straight back in before the interface has caught up.
            if (unmuting) _guard?.Rearm();
        }
    }

    /// <summary>
    /// Raised on the render thread when the output's level ran away and it was silenced. The
    /// node stays live, so a retry does not reopen it; only unmuting lets sound through.
    /// </summary>
    public event EventHandler<GraphErrorEventArgs>? Runaway;

    /// <summary>Raised on the render thread when the output dipped itself to drain a loop.</summary>
    public event EventHandler<GraphErrorEventArgs>? Dipped;

    private float _pan;

    /// <summary>Balance of the summed mix leaving this endpoint, from -1 to +1.</summary>
    public float Pan
    {
        get => _pan;
        set
        {
            _pan = Math.Clamp(value, -1f, 1f);
            var gain = _gain;
            if (gain is not null) gain.Pan = _pan;
        }
    }

    public float ReadPeak() => _gain?.ReadPeak() ?? 0f;

    public event EventHandler<GraphErrorEventArgs>? Failed;

    public void SetConnections(IEnumerable<Connection> connections)
    {
        lock (_sync)
        {
            _connections.Clear();
            _connections.AddRange(connections);
            RebuildMix();
        }
    }

    public bool Start(out string? error)
    {
        lock (_sync)
        {
            StopCore();

            _device = _devices.TryGetDevice(DeviceId);
            if (_device is null)
            {
                Status = NodeStatus.Waiting;
                StatusDetail = "Waiting for this device";
                StatusHint = null;
                error = StatusDetail;
                return false;
            }

            // Low latency mode, on real hardware only: exclusive at the buffer setting, then the
            // device's smallest shared period if exclusive is refused. Normal mode and virtual
            // cables take Windows' ordinary shared stream below. A cable moves audio in the rhythm
            // of whatever records its other end, and a 3 ms stream into one ran dry seven times in
            // sixteen seconds on a real machine while something was recording it.
            LowLatencyRender? fast = null;
            bool exclusiveRefused = false;
            if (LowLatencyMode && !DeviceSourceNode.IsVirtual(_device, Audio.AudioSourceKind.Render))
            {
                try { fast = new LowLatencyRender(DeviceId, BufferMilliseconds, exclusive: true, DeviceSourceNode.DeviceFormat(_device), timed: _exclusiveTimed); }
                catch { fast = null; exclusiveRefused = true; }

                if (fast is null)
                {
                    try { fast = new LowLatencyRender(DeviceId, BufferMilliseconds); }
                    catch { fast = null; }
                }
            }

            // Everything else, normal mode and every virtual cable, takes an ordinary shared
            // stream topped up on LoopIt7's own timer from what the device has really played.
            // Waiting for the engine to wake the output instead crackled on a Behringer, whose
            // driver wakes it in lumps; timed is what made low latency mode clean on it.
            if (fast is null)
            {
                try { fast = new LowLatencyRender(DeviceId, BufferMilliseconds, exclusive: false, timed: true); }
                catch { fast = null; }
            }

            try
            {
                if (fast is not null)
                {
                    SampleRate = fast.SampleRate;
                    Channels = fast.Channels;
                }
                else
                {
                    var mixFormat = _device.AudioClient.MixFormat;
                    SampleRate = mixFormat.SampleRate;
                    Channels = mixFormat.Channels;
                }

                // The mixer works in stereo at the endpoint rate. Channel mapping is the very
                // last step, so a 7.1 receiver and a mono headset take the same summed pair.
                _mixer = new MixSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2));
                _gain = new SmoothGainSampleProvider(_mixer);
                ApplyGain();
                _gain.ResetToTarget();

                // Last before the device, so it hears exactly what is being sent.
                _guard = new RunawayGuardSampleProvider(_gain);
                _guard.Runaway += OnRunaway;
                _guard.Dipped += OnDipped;

                ISampleProvider chain = _guard;
                if (Channels != 2) chain = new ChannelMapSampleProvider(chain, Channels);

                RebuildMix();

                if (fast is not null)
                {
                    fast.PlaybackStopped += OnPlaybackStopped;
                    fast.Init(chain);
                    fast.Play();
                    _fast = fast;
                    PeriodMilliseconds = fast.PeriodMilliseconds;
                    LatencyText = fast.Describe() + (exclusiveRefused ? ", exclusive refused" : string.Empty);
                }
                else
                {
                    // An ordinary shared stream never runs faster than the device period, 10 ms
                    // on almost every device, so a smaller request left one period of buffer and
                    // any hiccup on this thread played as a crackle. Two periods is the floor.
                    int period = DevicePeriodMilliseconds(_device);
                    int latency = Math.Max(BufferMilliseconds, period * 2);

                    _player = new WasapiOut(_device, AudioClientShareMode.Shared, true, latency);
                    _player.PlaybackStopped += OnPlaybackStopped;
                    _player.Init(chain);
                    _player.Play();
                    PeriodMilliseconds = period;
                    LatencyText = $"shared · {latency} ms";
                }

                Status = NodeStatus.Live;
                StatusDetail = null;
                StatusHint = null;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                if (fast is not null && _fast is null) fast.Dispose();

                // Asked before StopCore, which lets go of the device the owner is looked up on.
                var problem = DeviceProblem.From(ex, _device, recording: false);
                StopCore();
                Status = problem.Waiting ? NodeStatus.Waiting : NodeStatus.Failed;
                StatusDetail = problem.Detail;
                StatusHint = problem.Hint;
                error = StatusDetail;
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
            Status = NodeStatus.Idle;
            StatusDetail = null;
            StatusHint = null;
        }
    }

    /// <summary>Rebuilds the mixer inputs. Called when cables are patched or unpatched.</summary>
    private void RebuildMix()
    {
        var mixer = _mixer;
        if (mixer is null || SampleRate <= 0) return;

        mixer.SetInputs(_connections.Select(c => c.CreateTail(SampleRate)));
    }

    private void ApplyGain()
    {
        var gain = _gain;
        if (gain is null) return;

        gain.TargetGain = _muted ? 0f : _linearGain;
        gain.Pan = _pan;
    }

    /// <summary>One line for the audio log: what the output got and how its wakeups went.</summary>
    public string TakeDiagnostics()
    {
        string device = $"{DeviceSourceNode.SafeName(_device)} [{SampleRate} Hz, {Channels} ch]";
        var fast = _fast;
        if (fast is not null)
        {
            var (wakeups, dry, maxGap, maxFill) = fast.TakeStats();
            return $"{device} {fast.Describe()}; {wakeups} wakeups, {dry} ran dry, longest gap {maxGap:0.0} ms, largest lump the device took {fast.LargestLumpMilliseconds:0.0} ms, slowest mix {maxFill:0.00} ms";
        }

        return _player is null ? $"{device} not open" : $"{device} {LatencyText} (ordinary output)";
    }

    /// <summary>The engine period, or the usual 10 ms when the device will not say.</summary>
    private static int DevicePeriodMilliseconds(MMDevice device)
    {
        try
        {
            long period = device.AudioClient.DefaultDevicePeriod;
            return period > 0 ? (int)Math.Ceiling(period / 10000.0) : 10;
        }
        catch
        {
            return 10;
        }
    }

    private void OnRunaway(object? sender, EventArgs e) =>
        Runaway?.Invoke(this, new GraphErrorEventArgs(Id,
            "muted because its level kept building up again and again, which is a feedback " +
            "loop. Something is sending this output back into an input on the page, for " +
            "example VoiceMeeter sending a bus into LoopIt7 Cable. Fix that, then unmute it."));

    private void OnDipped(object? sender, EventArgs e) =>
        Dipped?.Invoke(this, new GraphErrorEventArgs(Id,
            "kept getting louder by itself, like a feedback loop, so LoopIt7 dipped it for a " +
            "moment to clear it. If it keeps happening, something is sending this output back " +
            "into an input."));

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is CoalescedWakeupsException)
        {
            // The driver lumps its wakeups together: reopen timed. Off the render thread, which
            // is the one finishing here and which reopening has to wait for.
            _exclusiveTimed = true;
            ThreadPool.QueueUserWorkItem(state => Start(out string? ignored));
            return;
        }

        if (e.Exception is null) return;
        var problem = DeviceProblem.From(e.Exception, _device, recording: false);
        Status = NodeStatus.Failed;
        StatusDetail = problem.Detail;
        StatusHint = problem.Hint;
        Failed?.Invoke(this, new GraphErrorEventArgs(Id, StatusDetail));
    }

    private void StopCore()
    {
        if (_fast is not null)
        {
            _fast.PlaybackStopped -= OnPlaybackStopped;
            try { _fast.Dispose(); } catch { }
            _fast = null;
        }

        if (_player is not null)
        {
            _player.PlaybackStopped -= OnPlaybackStopped;
            try { _player.Stop(); } catch { }
            try { _player.Dispose(); } catch { }
            _player = null;
        }

        if (_guard is not null)
        {
            _guard.Runaway -= OnRunaway;
            _guard.Dipped -= OnDipped;
        }

        _guard = null;
        _mixer = null;
        _gain = null;

        if (_device is not null)
        {
            try { _device.Dispose(); } catch { }
            _device = null;
        }
    }

    public void Dispose()
    {
        lock (_sync) StopCore();
    }
}
