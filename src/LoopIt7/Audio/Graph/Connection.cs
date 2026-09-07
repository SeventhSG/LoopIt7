using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LoopIt7.Audio.Graph;

/// <summary>
/// A cable. It owns the ring buffer that decouples the source's capture clock from the
/// destination's render clock, plus this cable's own trim, mute and alignment delay.
/// <para>
/// Resampling happens here rather than at a global engine rate, so audio crosses a rate
/// boundary exactly once on its way from a source to a destination.
/// </para>
/// </summary>
internal sealed class Connection : IDisposable
{
    private const int RingMilliseconds = 400;

    private readonly object _sync = new();

    private BufferedWaveProvider _ring;
    private DelaySampleProvider _delay;
    private SmoothGainSampleProvider _gain;
    private WaveFormat _sourceFormat;
    private byte[] _trimScratch = [];

    public Connection(string id, string sourceId, string destinationId, WaveFormat sourceFormat)
    {
        Id = id;
        SourceId = sourceId;
        DestinationId = destinationId;
        _sourceFormat = sourceFormat;
        (_ring, _delay, _gain) = BuildChain(sourceFormat);
    }

    public string Id { get; }
    public string SourceId { get; }
    public string DestinationId { get; }

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
        set { _muted = value; ApplyGain(); }
    }

    private int _delayMs;
    public int DelayMilliseconds
    {
        get => _delayMs;
        set
        {
            _delayMs = Math.Clamp(value, 0, DelaySampleProvider.MaxDelayMilliseconds);
            _delay.DelayMilliseconds = _delayMs;
        }
    }

    /// <summary>Post trim peak since the last read, so a cable can show what it is carrying.</summary>
    public float ReadPeak() => _gain.ReadPeak();

    public double QueuedMilliseconds => _ring.BufferedDuration.TotalMilliseconds;

    /// <summary>
    /// Builds the tail of the chain for one destination. Called by the destination when it
    /// opens, because only then is the endpoint's sample rate known.
    /// </summary>
    public ISampleProvider CreateTail(int destinationSampleRate)
    {
        lock (_sync)
        {
            _ring.ClearBuffer();
            _gain.ResetToTarget();

            ISampleProvider tail = _gain;
            if (_sourceFormat.SampleRate != destinationSampleRate)
            {
                tail = new WdlResamplingSampleProvider(tail, destinationSampleRate);
            }

            return tail;
        }
    }

    /// <summary>Rebuilds the head of the chain after the source reopened at a different rate.</summary>
    public void Reformat(WaveFormat sourceFormat)
    {
        lock (_sync)
        {
            if (sourceFormat.SampleRate == _sourceFormat.SampleRate &&
                sourceFormat.Channels == _sourceFormat.Channels)
            {
                return;
            }

            _sourceFormat = sourceFormat;
            (_ring, _delay, _gain) = BuildChain(sourceFormat);
            _delay.DelayMilliseconds = _delayMs;
            ApplyGain();
        }
    }

    /// <summary>
    /// Called from the source's capture thread. Never blocks: if a destination has stalled,
    /// the ring drops rather than hold the source up.
    /// </summary>
    public void Write(byte[] bus, int count, int targetQueueMs)
    {
        var ring = _ring;
        ring.AddSamples(bus, 0, count);

        // Two clocks that were never synchronised will drift apart all evening. Trimming the
        // queue back is what keeps a monitor mix from sliding a quarter second late.
        double queued = ring.BufferedDuration.TotalMilliseconds;
        if (queued <= targetQueueMs) return;

        int excess = (int)((queued - targetQueueMs) / 1000.0 * ring.WaveFormat.AverageBytesPerSecond);
        excess -= excess % ring.WaveFormat.BlockAlign;
        if (excess <= 0) return;

        if (_trimScratch.Length < excess) _trimScratch = new byte[excess];
        ring.Read(_trimScratch, 0, excess);
    }

    private static (BufferedWaveProvider, DelaySampleProvider, SmoothGainSampleProvider) BuildChain(WaveFormat format)
    {
        var ring = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(RingMilliseconds),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        var delay = new DelaySampleProvider(ring.ToSampleProvider());
        var gain = new SmoothGainSampleProvider(delay);
        return (ring, delay, gain);
    }

    private void ApplyGain() => _gain.TargetGain = _muted ? 0f : _linearGain;

    public void Dispose()
    {
        lock (_sync)
        {
            _ring.ClearBuffer();
        }
    }
}
