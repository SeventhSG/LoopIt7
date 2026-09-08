using System.Diagnostics;
using LoopIt7.Audio.Interop;
using NAudio.Wave;

namespace LoopIt7.Audio.Graph;

/// <summary>
/// A virtual output. Cables arrive at it the way they arrive at a real endpoint, it sums
/// them, and then the sum leaves again down cables of its own. One box you can name, feed
/// from several programs, and fan out to as many real outputs as you like.
/// <para>
/// It is not a Windows audio endpoint. No user mode program can add one of those, and this
/// does not pretend otherwise: it is a box inside LoopIt7, and programs reach it by being
/// captured, not by selecting it in their own settings.
/// </para>
/// <para>
/// Every other node in the graph rides a clock somebody else owns: a capture endpoint's or a
/// render endpoint's. A virtual output has neither, so it keeps its own on a timer thread and
/// renders whatever the wall clock says is due. Downstream cables trim their queues against
/// the destination's real clock exactly as they do for a microphone, because this is one more
/// clock that was never synchronised with theirs.
/// </para>
/// </summary>
internal sealed class BusNode : SourceNode
{
    /// <summary>
    /// The rate a virtual output works at. Fixed rather than negotiated: it has no hardware
    /// to agree with, and 48 kHz is what every endpoint on a modern machine resamples to
    /// anyway.
    /// </summary>
    public const int BusSampleRate = 48000;

    private static readonly WaveFormat BusFormat = WaveFormat.CreateIeeeFloatWaveFormat(BusSampleRate, 2);

    private readonly object _sync = new();
    private readonly List<Connection> _incoming = [];
    private readonly MixSampleProvider _mixer = new(BusFormat);

    private Thread? _pump;
    private volatile bool _pumping;
    private long _renderedFrames;
    private float[] _block = [];
    private byte[] _bytes = [];

    public BusNode(string id, string displayName) : base(id, displayName)
    {
    }

    public override SourceKind Kind => SourceKind.Bus;

    /// <summary>How much audio the pump renders per pass. Follows the engine buffer setting.</summary>
    public int BlockMilliseconds { get; set; } = 10;

    /// <summary>
    /// Frames the pump has rendered since it started. Read by the self test, which compares
    /// it against elapsed time to prove the box keeps its own clock honestly.
    /// </summary>
    public long RenderedFrames => Interlocked.Read(ref _renderedFrames);

    /// <summary>How many cables are currently feeding this box.</summary>
    public int IncomingCount
    {
        get { lock (_sync) return _incoming.Count; }
    }

    /// <summary>
    /// Replaces the set of cables arriving here. Safe while the pump is running: the mixer
    /// swaps its inputs under a lock and the render pass takes whichever set it finds.
    /// </summary>
    public void SetIncoming(IEnumerable<Connection> connections)
    {
        lock (_sync)
        {
            _incoming.Clear();
            _incoming.AddRange(connections);
            RebuildMix();
        }
    }

    public override bool Start(out string? error)
    {
        Stop();

        NativeChannels = 2;
        PrepareBus(BusFormat);
        Interlocked.Exchange(ref _renderedFrames, 0);

        lock (_sync) RebuildMix();

        TimerResolution.Acquire();

        _pumping = true;
        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = $"LoopIt7 virtual output {DisplayName}"
        };

        _pump.Start();

        Status = NodeStatus.Live;
        StatusDetail = null;
        error = null;
        return true;
    }

    public override void Stop()
    {
        var pump = _pump;
        if (pump is not null)
        {
            _pumping = false;
            _pump = null;

            try { pump.Join(500); } catch { }
            TimerResolution.Release();
        }

        if (Status == NodeStatus.Live) Status = NodeStatus.Idle;
    }

    private void RebuildMix() => _mixer.SetInputs(_incoming.Select(c => c.CreateTail(BusSampleRate)));

    /// <summary>
    /// The clock. Renders whatever the elapsed wall time says is owed, so an hour of running
    /// leaves the box at 48 kHz on average rather than at whatever the thread scheduler felt
    /// like handing out.
    /// </summary>
    private void Pump()
    {
        int framesPerBlock = Math.Max(64, BusSampleRate * BlockMilliseconds / 1000);

        // A late wake up is caught up with, but only so far. Rendering a huge burst after a
        // stall would just be trimmed away downstream and would sound worse than the gap.
        int maxFrames = framesPerBlock * 4;

        var clock = Stopwatch.StartNew();
        long rendered = 0;

        while (_pumping)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * BusSampleRate);
            long deficit = due - rendered;

            if (deficit < framesPerBlock)
            {
                Thread.Sleep(1);
                continue;
            }

            int frames = (int)Math.Min(deficit, maxFrames);
            int samples = frames * 2;
            int byteCount = samples * 4;

            if (_block.Length < samples) _block = new float[samples];
            if (_bytes.Length < byteCount) _bytes = new byte[byteCount];

            _mixer.Read(_block, 0, samples);
            Buffer.BlockCopy(_block, 0, _bytes, 0, byteCount);

            // Same path a capture takes: the node fader, then every cable leaving the box.
            Distribute(_bytes, byteCount, BusFormat);

            rendered += frames;
            Interlocked.Exchange(ref _renderedFrames, rendered);
        }
    }
}
