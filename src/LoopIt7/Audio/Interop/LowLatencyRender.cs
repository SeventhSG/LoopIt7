using System.Runtime.InteropServices;
using NAudio.Wave;

namespace LoopIt7.Audio.Interop;

/// <summary>
/// A playback endpoint opened at its smallest engine period, on a Pro Audio thread: 6.6 ms of
/// audio waiting in the device at a 3 ms period, where an ordinary shared stream keeps 20 ms.
/// Most hardware only offers 10 ms in shared mode, and there it matches the ordinary stream.
/// <para>
/// The constructor throws when the device will not run in low latency mode, and the output
/// falls back to NAudio's shared stream.
/// </para>
/// </summary>
/// <summary>
/// The device woke a woken exclusive stream far less often than its period promises, so it
/// has to be reopened on a timer. Not a failure the user needs to hear about.
/// </summary>
internal sealed class CoalescedWakeupsException(int wakeups, double promised)
    : Exception($"The device woke the output {wakeups} times where its period promised {promised:0}.");

internal sealed class LowLatencyRender : IDisposable
{
    private const uint WaitTimeoutMs = 200;

    private readonly LowLatencyStream _stream;
    private readonly IAudioRenderClient _render;
    private readonly IntPtr _event;
    private ISampleProvider? _source;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private bool _disposed;

    /// <param name="timed">Exclusive only: top up on a timer instead of trusting the device's wakeups.</param>
    public LowLatencyRender(string deviceId, int requestedMilliseconds, bool exclusive = false, byte[]? deviceFormat = null, bool timed = false)
    {
        _stream = exclusive
            ? LowLatencyInterop.OpenExclusive(deviceId, deviceFormat, requestedMilliseconds, timed)
            : timed
                ? LowLatencyInterop.OpenSharedTimed(deviceId)
                : LowLatencyInterop.Open(deviceId, requestedMilliseconds);
        try
        {
            var mix = _stream.Mix;
            bool writable = mix.IsFloat ? mix.BitsPerSample == 32 : mix.BitsPerSample is 16 or 24 or 32;
            if (!writable) throw new NotSupportedException($"{mix.BitsPerSample} bit mix formats go the ordinary way.");

            // A timed stream still gets an event, set by Stop so the loop wakes at once; the
            // device just never signals it.
            _event = LowLatencyInterop.CreateEventW(IntPtr.Zero, false, false, null);
            if (_event == IntPtr.Zero) throw new InvalidOperationException("Could not create the render event.");

            if (!_stream.Timed) Marshal.ThrowExceptionForHR(_stream.Client.SetEventHandle(_event));
            Marshal.ThrowExceptionForHR(_stream.Client.GetService(LowLatencyInterop.IidAudioRenderClient, out object service));
            _render = (IAudioRenderClient)service;
        }
        catch
        {
            if (_event != IntPtr.Zero) LowLatencyInterop.CloseHandle(_event);
            _stream.Dispose();
            throw;
        }
    }

    public int SampleRate => _stream.Mix.SampleRate;
    public int Channels => _stream.Mix.Channels;
    public double PeriodMilliseconds => _stream.PeriodMilliseconds;

    /// <summary>"exclusive · 144 samples (3.0 ms)", for the box.</summary>
    public string Describe() => _stream.Describe(_keptFrames);

    /// <summary>What a timed stream keeps queued in the device, set from the lumps it measures.</summary>
    private volatile int _keptFrames;

    /// <summary>The most the device took between two top ups: how lumpy its driver is, in ms.</summary>
    public double LargestLumpMilliseconds => _largestLumpFrames * 1000.0 / SampleRate;
    private volatile int _largestLumpFrames;

    private int _wakeups;
    private int _dry;
    private double _maxGapMs;
    private double _maxFillMs;
    private long _lastWakeTicks;

    /// <summary>
    /// Wakeups, how often the device had run dry (shared mode only; exclusive cannot tell),
    /// the longest wait between wakeups and the longest the mix took to compute, since the
    /// last call. For the audio log.
    /// </summary>
    public (int Wakeups, int Dry, double MaxGapMs, double MaxFillMs) TakeStats()
    {
        var stats = (_wakeups, _dry, _maxGapMs, _maxFillMs);
        _wakeups = _dry = 0;
        _maxGapMs = _maxFillMs = 0;
        return stats;
    }

    /// <summary>Raised when the stream stops on its own, with the reason.</summary>
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    /// <summary>The source must be float at the device's rate and channel count.</summary>
    public void Init(ISampleProvider source)
    {
        if (source.WaveFormat.SampleRate != SampleRate || source.WaveFormat.Channels != Channels)
        {
            throw new ArgumentException("The render source must match the device's mix format.");
        }

        _source = source;
    }

    public void Play()
    {
        if (_source is null) throw new InvalidOperationException("Init first.");
        if (_thread is not null) return;

        // One period of whatever the mix has, before the clock starts, so the first deadline
        // is not already missed. Exclusive mode wants the whole buffer, which is one period.
        Fill(_stream.PeriodFrames, new float[_stream.BufferFrames * Channels]);
        Marshal.ThrowExceptionForHR(_stream.Client.Start());

        _stopRequested = false;
        _thread = new Thread(Pump) { IsBackground = true, Name = "LoopIt7 low latency render" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _stopRequested = true;
        LowLatencyInterop.SetEvent(_event);

        var thread = _thread;
        _thread = null;
        thread?.Join(2000);

        try { _stream.Client.Stop(); } catch { }
    }

    private void Pump()
    {
        IntPtr proAudio = LowLatencyInterop.EnterProAudio();
        Exception? failure = null;

        // The whole buffer the engine gave, which in low latency mode is only a little over two
        // periods. Measured on a 3 ms cable, keeping two periods ran dry four times in five
        // seconds and keeping the full 6.6 ms buffer once.
        int target = _stream.BufferFrames;
        var scratch = new float[_stream.BufferFrames * Channels];

        // Timed: start at 15 ms queued (20 in shared mode, where the engine takes a whole period
        // at a time), never the whole buffer, which a driver may round up well past that. After a
        // second the target becomes the biggest lump the device has taken between two top ups
        // plus 4 ms: only as much as this particular driver needs, and it grows again if a bigger
        // lump ever turns up.
        bool timed = _stream.Timed;
        int margin = SampleRate * 4 / 1000;
        int floor = _stream.Exclusive ? _stream.PeriodFrames * 2 : SampleRate * 5 / 1000;
        if (timed) target = Math.Min(_stream.BufferFrames, SampleRate * (_stream.Exclusive ? 15 : 20) / 1000);
        _keptFrames = timed ? target : 0;
        int lastPaddingAfterWrite = -1;
        int largestLump = 0;
        if (timed) TimerResolution.Acquire();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int wakeupsSinceStart = 0;
        bool checkedWakeups = false;

        try
        {
            while (!_stopRequested)
            {
                // Woken: the device says when. Timed: every millisecond or so, and the padding
                // says how much it has really played.
                LowLatencyInterop.WaitForSingleObject(_event, timed ? 1u : WaitTimeoutMs);
                if (_stopRequested) break;
                wakeupsSinceStart++;

                // A woken exclusive stream is only right if the device wakes it every period.
                // After a second, fewer than 80% of the wakeups the period promises means the
                // driver lumps them together and every missing one is a period it played stale;
                // give up and let the output reopen timed.
                if (_stream.Exclusive && !timed && !checkedWakeups)
                {
                    double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (elapsed >= 1000)
                    {
                        checkedWakeups = true;
                        double promised = elapsed / _stream.PeriodMilliseconds;
                        if (wakeupsSinceStart < promised * 0.8)
                            throw new CoalescedWakeupsException(wakeupsSinceStart, promised);
                    }
                }

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_lastWakeTicks != 0)
                {
                    double gap = (now - _lastWakeTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (gap > _maxGapMs) _maxGapMs = gap;
                }

                _lastWakeTicks = now;
                _wakeups++;

                int frames;
                if (_stream.Exclusive && !timed)
                {
                    // Exclusive event mode: every wakeup takes exactly one whole buffer.
                    frames = _stream.BufferFrames;
                }
                else
                {
                    Marshal.ThrowExceptionForHR(_stream.Client.GetCurrentPadding(out uint padding));
                    if (padding == 0) _dry++;

                    if (timed)
                    {
                        // What the device took since the last top up is one of its lumps.
                        if (lastPaddingAfterWrite >= 0)
                        {
                            int lump = lastPaddingAfterWrite - (int)padding;
                            if (lump > largestLump)
                            {
                                largestLump = lump;
                                _largestLumpFrames = lump;
                            }
                        }

                        double running = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        if (running >= 1000)
                        {
                            target = Math.Clamp(largestLump + margin, floor, _stream.BufferFrames);
                            _keptFrames = target;
                        }
                    }

                    frames = target - (int)padding;
                    lastPaddingAfterWrite = (int)padding + Math.Max(0, frames);
                }

                if (frames > 0) Fill(frames, scratch);

                double fill = (System.Diagnostics.Stopwatch.GetTimestamp() - now) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (fill > _maxFillMs) _maxFillMs = fill;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (timed) TimerResolution.Release();
            LowLatencyInterop.LeaveProAudio(proAudio);
            if (!_stopRequested) PlaybackStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }

    private void Fill(int frames, float[] scratch)
    {
        int samples = frames * Channels;
        _source!.Read(scratch, 0, samples);

        Marshal.ThrowExceptionForHR(_render.GetBuffer((uint)frames, out IntPtr data));

        var mix = _stream.Mix;
        if (mix.IsFloat && mix.BitsPerSample == 32)
        {
            Marshal.Copy(scratch, 0, data, samples);
        }
        else
        {
            WriteInteger(scratch, samples, data, mix.BitsPerSample);
        }

        Marshal.ThrowExceptionForHR(_render.ReleaseBuffer((uint)frames, 0));
    }

    /// <summary>For the rare device whose shared mix format is not float.</summary>
    private static void WriteInteger(float[] samples, int count, IntPtr destination, int bits)
    {
        for (int i = 0; i < count; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            switch (bits)
            {
                case 16:
                    Marshal.WriteInt16(destination, i * 2, (short)(clamped * short.MaxValue));
                    break;
                case 24:
                    int value = (int)(clamped * 8388607f);
                    Marshal.WriteByte(destination, i * 3, (byte)value);
                    Marshal.WriteByte(destination, i * 3 + 1, (byte)(value >> 8));
                    Marshal.WriteByte(destination, i * 3 + 2, (byte)(value >> 16));
                    break;
                default:
                    Marshal.WriteInt32(destination, i * 4, (int)(clamped * int.MaxValue));
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        try { Marshal.ReleaseComObject(_render); } catch { }
        LowLatencyInterop.CloseHandle(_event);
        _stream.Dispose();
    }
}
