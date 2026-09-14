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
            _reader.Reset();
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

    /// <summary>Audio thrown away since the cable was built, in milliseconds. Only a stall does that.</summary>
    public double TrimmedMilliseconds { get; private set; }

    /// <summary>Times the reader ran dry and waited for the queue to refill.</summary>
    public int Underruns => _reader.Underruns;

    /// <summary>Blocks the reader stretched or squeezed by one frame to follow the other clock.</summary>
    public int DriftCorrections => _reader.Corrections;

    /// <summary>How much the queue holds just before the destination pulls, averaged, in ms.</summary>
    public double SettledQueueMilliseconds => _reader.SettledMilliseconds;

    /// <summary>A queue this far past what the reader needs means the destination stalled.</summary>
    private const double StallMilliseconds = 100;

    private volatile float _largestPacketMs;
    private DriftReader _reader = null!;

    /// <summary>Milliseconds on a steady clock. Replaceable so the tests can run on simulated time.</summary>
    internal Func<double> Clock { get; set; } =
        () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Called from the source's capture thread. Never blocks: if a destination has stalled,
    /// the ring drops rather than hold the source up.
    /// <para>
    /// Clock drift is no longer handled here. The reader follows it one frame at a time, so
    /// the queue can stay at one packet of margin instead of a large safety buffer. What is
    /// left for the writer is the case where the destination stopped pulling altogether.
    /// </para>
    /// </summary>
    private long _framesIn;
    private long _framesOutTaken;

    /// <summary>Frames that arrived since the destination opened: half of the measured clock ratio.</summary>
    private long _arrivedSinceStart;

    /// <summary>
    /// Frames that arrived from the source and frames the destination took, since the last call.
    /// Equal over a few seconds when both clocks agree; far apart means one side runs at the
    /// wrong speed, which no queue can hide.
    /// </summary>
    public (long In, long Out) TakeRates()
    {
        long frameIn = Interlocked.Exchange(ref _framesIn, 0);
        long frameOut = Interlocked.Exchange(ref _framesOutTaken, 0);
        return (frameIn, frameOut);
    }

    public int SourceSampleRate => _sourceFormat.SampleRate;

    public void Write(byte[] bus, int count, int targetQueueMs)
    {
        var ring = _ring;
        Interlocked.Add(ref _framesIn, count / ring.WaveFormat.BlockAlign);

        // Just before a packet lands is when the queue is at its lowest. Counted as what is
        // left once the output's use since its last pull is taken off, so the margin shrinks
        // smoothly as the two clocks slide, rather than looking steady until a pull finds nothing.
        _reader.ObserveArrival(ring.BufferedDuration.TotalMilliseconds);

        ring.AddSamples(bus, 0, count);
        Interlocked.Add(ref _arrivedSinceStart, count / ring.WaveFormat.BlockAlign);

        float packetMs = (float)(count * 1000.0 / ring.WaveFormat.AverageBytesPerSecond);
        if (packetMs > _largestPacketMs) _largestPacketMs = packetMs;
        _reader.MinimumQueueMilliseconds = targetQueueMs;

        double needed = _reader.NeededMilliseconds;
        double queued = ring.BufferedDuration.TotalMilliseconds;
        if (queued <= needed + StallMilliseconds) return;

        int excess = (int)((queued - needed) / 1000.0 * ring.WaveFormat.AverageBytesPerSecond);
        excess -= excess % ring.WaveFormat.BlockAlign;
        if (excess <= 0) return;

        if (_trimScratch.Length < excess) _trimScratch = new byte[excess];
        int dropped = ring.Read(_trimScratch, 0, excess);
        TrimmedMilliseconds += dropped * 1000.0 / ring.WaveFormat.AverageBytesPerSecond;
    }

    private (BufferedWaveProvider, DelaySampleProvider, SmoothGainSampleProvider) BuildChain(WaveFormat format)
    {
        var ring = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(RingMilliseconds),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        _reader = new DriftReader(ring, this);
        var delay = new DelaySampleProvider(_reader);
        var gain = new SmoothGainSampleProvider(delay);
        return (ring, delay, gain);
    }

    /// <summary>
    /// Reads the ring for the destination, following the source's clock rather than trimming
    /// or padding against it.
    /// <para>
    /// What it steers is the lowest point the queue reaches, not its average. Running dry is
    /// the queue touching zero, and each one plays as a click, so every 200 ms it looks at how
    /// close the queue came to empty and aims to keep that a few milliseconds above it. The
    /// average swings with packet and pull sizes and following it had the reader hunting 260
    /// times a second and still running dry on a real machine.
    /// </para>
    /// <para>
    /// It follows the other clock with a variable rate resampler, NAudio's WDL in plain linear
    /// mode, whose rate a proportional and an integral term set from that lowest point. Ordinary
    /// drift is a few samples a second; a device that runs at the wrong speed altogether, a
    /// Behringer under VoiceMeeter on a real machine, was 8 percent out and anything nudging a
    /// sample at a time had to throw audio away instead, 24 seconds of it, each cut a crackle.
    /// The resampler follows up to 10 percent either way without a cut. Only when the queue
    /// actually runs out, a stalled source, does it hold silence until the queue has refilled.
    /// </para>
    /// </summary>
    private sealed class DriftReader : ISampleProvider
    {
        /// <summary>The furthest the reader will speed up or slow down the source, as a fraction.</summary>
        private const double MaxRatioOffset = 0.10;

        private readonly NAudio.Dsp.WdlResampler _resampler = new();

        /// <summary>Input frames per output frame, less one: positive plays the source faster.</summary>
        private double _ratioOffset;
        /// <summary>
        /// How far above empty the queue's lowest point is kept: 3 ms, or half a packet for a
        /// source that delivers in big ones, since a big packet arriving late leaves a big hole.
        /// A flat 3 ms ran dry eleven times a minute on 10 ms packets with a slow clock.
        /// </summary>
        private double SafetyMilliseconds => Math.Max(3.0, _owner._largestPacketMs * 0.5);
        private const double HysteresisMilliseconds = 1.0;

        /// <summary>
        /// Long enough that the lowest point is steady. At 200 ms a shared output, which takes
        /// anything from 3 to 6.6 ms a bite, made it jump about and the reader chased that noise.
        /// Real clocks drift a few samples a second, so half a second is still quick.
        /// </summary>
        private const double WindowMilliseconds = 500;

        /// <summary>How much of the queue error to close each window.</summary>
        private const double Proportional = 0.5;

        private readonly BufferedWaveProvider _ring;
        private readonly ISampleProvider _samples;
        private readonly Connection _owner;
        private readonly int _channels;
        private readonly double _framesPerMs;
        private bool _priming = true;
        private volatile float _largestPullMs;

        // The current window: the lowest the queue came, how long and how many pulls it covered.
        // The lowest is written by the writer's thread and taken by this one.
        private double _windowLowestMs = double.MaxValue;
        private double _windowMs;
        private int _windowPulls;
        private double _lastLowestMs = -1;
        private double _lastPullAtMs = -1;

        /// <summary>
        /// The lowest the queue was left after a pull this window, kept on this thread. A shared
        /// output that wakes late takes a big bite at once, which the arrival margin cannot see.
        /// </summary>
        private double _windowLowestAfterPullMs = double.MaxValue;

        public DriftReader(BufferedWaveProvider ring, Connection owner)
        {
            _ring = ring;
            _samples = ring.ToSampleProvider();
            _owner = owner;
            WaveFormat = _samples.WaveFormat;
            _channels = WaveFormat.Channels;
            _framesPerMs = WaveFormat.SampleRate / 1000.0;

            // Plain linear interpolation, no filter: the ratio stays within a few percent of one,
            // and WDL's filter would take the top off everything at 16 kHz for nothing.
            _resampler.SetMode(true, 0, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(false);
            _resampler.SetRates(WaveFormat.SampleRate, WaveFormat.SampleRate);
        }

        public WaveFormat WaveFormat { get; }

        public int Underruns { get; private set; }
        public int Corrections { get; private set; }

        /// <summary>A floor from the buffer setting. Set by the writer.</summary>
        public volatile int MinimumQueueMilliseconds;

        /// <summary>The lowest the queue came in the last window, in ms: its margin above running dry.</summary>
        public double SettledMilliseconds => Math.Max(0, _lastLowestMs);

        /// <summary>
        /// Called by the writer just before a packet is added: the queue then, less what the
        /// output has used since its last pull, is the real margin above running dry.
        /// </summary>
        public void ObserveArrival(double queuedMs)
        {
            double lastPull = _lastPullAtMs;
            if (_priming || lastPull < 0) return;

            double margin = queuedMs - (_owner.Clock() - lastPull);
            if (margin < _windowLowestMs) _windowLowestMs = margin;
        }

        /// <summary>
        /// What the queue needs just before a pull when it starts: one pull, one source packet
        /// and the safety margin. The writer trims only far past this.
        /// </summary>
        public double NeededMilliseconds =>
            Math.Max(MinimumQueueMilliseconds, _largestPullMs + _owner._largestPacketMs + SafetyMilliseconds)
            + (MeasuredRatioOffset() is null ? StartMarginMilliseconds : 0);

        /// <summary>
        /// Extra queue to start on, until the other clock has been measured: a device 8% slow
        /// drains 20 ms in the quarter second before the first estimate, and without it ran dry
        /// two or three times right at the start.
        /// </summary>
        private const double StartMarginMilliseconds = 15;

        /// <summary>A fresh start, when the destination opens: forget the other clock too.</summary>
        public void Reset()
        {
            _ratioOffset = 0;
            _producedSinceStart = 0;
            Interlocked.Exchange(ref _owner._arrivedSinceStart, 0);
            Refill();
        }

        /// <summary>Output frames since the destination opened, silence included: the other half.</summary>
        private long _producedSinceStart;

        /// <summary>
        /// The other clock's speed, measured rather than guessed: frames that arrived over frames
        /// the output asked for, since it opened. Over a second or two that ratio is the speed
        /// difference itself, a packet either way hardly moves it, and the queue term only has to
        /// trim around it. Learning it by trial left a device 8% off running dry while it learned.
        /// Null until there is a quarter of a second of it: waiting a full second left a device 8%
        /// slow running dry four times before the first estimate, and a quarter second is already
        /// within a percent or so, which the queue term covers while the count grows.
        /// </summary>
        private double? MeasuredRatioOffset()
        {
            long produced = _producedSinceStart;
            if (produced < WaveFormat.SampleRate / 4) return null;
            return Interlocked.Read(ref _owner._arrivedSinceStart) / (double)produced - 1.0;
        }

        /// <summary>
        /// After running dry: wait for the queue to refill, but keep the speed learned for the
        /// other clock. Forgetting it on every dry-out left a device 8% off running dry for ever.
        /// </summary>
        private void Refill()
        {
            _priming = true;
            _resampler.Reset();
            _windowLowestMs = double.MaxValue;
            _windowLowestAfterPullMs = double.MaxValue;
            _windowMs = 0;
            _windowPulls = 0;
            _lastPullAtMs = -1;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / _channels;
            float pullMs = (float)(frames / _framesPerMs);
            if (pullMs > _largestPullMs) _largestPullMs = pullMs;
            _producedSinceStart += frames;

            int bytesPerFrame = _ring.WaveFormat.BlockAlign;
            int availableFrames = _ring.BufferedBytes / bytesPerFrame;

            if (_priming)
            {
                // Not before the first packet has said how big packets are: primed on a guess,
                // a 10 ms microphone started with 6 ms queued and ran dry on the first late pull.
                if (_owner._largestPacketMs <= 0 || availableFrames < NeededMilliseconds * _framesPerMs)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                _priming = false;
            }

            // Ask the resampler how much source it needs for this block at the current rate.
            _resampler.SetRates(WaveFormat.SampleRate * (1 + _ratioOffset), WaveFormat.SampleRate);
            int take = _resampler.ResamplePrepare(frames, _channels, out float[] input, out int inputOffset);

            if (availableFrames < take)
            {
                // Ran dry: hold silence until the queue has refilled. Running dry also says the
                // source is slower than the rate assumed, so slow down straight away rather than
                // wait for the window.
                Array.Clear(buffer, offset, count);
                Underruns++;
                if (MeasuredRatioOffset() is { } measured) _ratioOffset = Math.Clamp(measured, -MaxRatioOffset, MaxRatioOffset);
                Refill();
                return count;
            }

            _lastPullAtMs = _owner.Clock();
            Interlocked.Add(ref _owner._framesOutTaken, take);
            double leftoverMs = (availableFrames - take) / _framesPerMs;
            if (leftoverMs < _windowLowestAfterPullMs) _windowLowestAfterPullMs = leftoverMs;
            Observe(pullMs);

            int read = _samples.Read(input, inputOffset, take * _channels) / _channels;
            int produced = _resampler.ResampleOut(buffer, offset, read, frames, _channels);
            if (produced < frames) Array.Clear(buffer, offset + produced * _channels, (frames - produced) * _channels);
            return count;
        }

        /// <summary>
        /// Adds this pull to the window, and at the end of each window sets the resampling rate
        /// that brings the queue's lowest point back to just above the safety margin.
        /// </summary>
        private void Observe(double pullMs)
        {
            _windowMs += pullMs;
            _windowPulls++;
            if (_windowMs < WindowMilliseconds) return;

            double arrival = Interlocked.Exchange(ref _windowLowestMs, double.MaxValue);
            double lowest = Math.Min(arrival, _windowLowestAfterPullMs);
            double windowMs = _windowMs;
            _windowLowestAfterPullMs = double.MaxValue;
            _windowMs = 0;
            _windowPulls = 0;
            if (arrival == double.MaxValue) return;   // no packet arrived this window

            // The base is the measured speed of the other clock. On top of it the queue error, as
            // a fraction of the window, is the extra speed change that would close it in one
            // window, and half of it is applied; closing all of it at once overshot on a real
            // machine. No integral: the measurement already holds the drift, and an integral wound
            // up while the queue was high kept the reader 1 to 2% fast long after, draining the
            // queue until it ran dry, over and over. Inside the band only the base is applied.
            double middle = SafetyMilliseconds + HysteresisMilliseconds;
            double error = (lowest - middle) / windowMs;
            double measured = MeasuredRatioOffset() ?? 0;

            double offset = measured + (Math.Abs(lowest - middle) > HysteresisMilliseconds ? error * Proportional : 0);

            _ratioOffset = Math.Clamp(offset, -MaxRatioOffset, MaxRatioOffset);
            if (_ratioOffset != 0) Corrections++;

            _lastLowestMs = lowest;
        }
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
