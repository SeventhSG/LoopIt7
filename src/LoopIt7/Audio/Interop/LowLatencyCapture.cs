using System.Runtime.InteropServices;
using NAudio.Wave;

namespace LoopIt7.Audio.Interop;

/// <summary>
/// A recording endpoint opened at its smallest engine period, on a Pro Audio thread. Hands
/// the engine one packet per period, 3 ms or less where the device allows it, instead of the
/// 10 ms an ordinary shared capture delivers.
/// <para>
/// Implements <see cref="IWaveIn"/> so an input box cannot tell it from NAudio's capture.
/// The constructor throws when the device will not run in low latency mode, and the caller
/// falls back to the ordinary path.
/// </para>
/// </summary>
internal sealed class LowLatencyCapture : IWaveIn
{
    private const uint WaitTimeoutMs = 200;

    private readonly LowLatencyStream _stream;
    private readonly IAudioCaptureClient _capture;
    private readonly IntPtr _event;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private bool _disposed;

    public LowLatencyCapture(string deviceId, int requestedMilliseconds, bool exclusive = false, byte[]? deviceFormat = null)
    {
        _stream = exclusive
            ? LowLatencyInterop.OpenExclusive(deviceId, deviceFormat, requestedMilliseconds)
            : LowLatencyInterop.Open(deviceId, requestedMilliseconds);
        try
        {
            var mix = _stream.Mix;
            WaveFormat = mix.IsFloat
                ? WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels)
                : new WaveFormat(mix.SampleRate, mix.BitsPerSample, mix.Channels);

            _event = LowLatencyInterop.CreateEventW(IntPtr.Zero, false, false, null);
            if (_event == IntPtr.Zero) throw new InvalidOperationException("Could not create the capture event.");

            Marshal.ThrowExceptionForHR(_stream.Client.SetEventHandle(_event));
            Marshal.ThrowExceptionForHR(_stream.Client.GetService(LowLatencyInterop.IidAudioCaptureClient, out object service));
            _capture = (IAudioCaptureClient)service;
        }
        catch
        {
            if (_event != IntPtr.Zero) LowLatencyInterop.CloseHandle(_event);
            _stream.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat { get; set; }

    /// <summary>The engine period this stream runs at, in milliseconds.</summary>
    public double PeriodMilliseconds => _stream.PeriodMilliseconds;

    /// <summary>"exclusive · 144 samples (3.0 ms)", for the box.</summary>
    public string Describe() => _stream.Describe();

    private int _packets;
    private double _maxGapMs;
    private long _lastPacketTicks;

    /// <summary>Packets and the longest wait between two, since the last call. For the audio log.</summary>
    public (int Packets, double MaxGapMs) TakeStats()
    {
        var stats = (_packets, _maxGapMs);
        _packets = 0;
        _maxGapMs = 0;
        return stats;
    }

    private void CountPacket()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastPacketTicks != 0)
        {
            double gap = (now - _lastPacketTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (gap > _maxGapMs) _maxGapMs = gap;
        }

        _lastPacketTicks = now;
        _packets++;
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (_thread is not null) return;

        _stopRequested = false;
        Marshal.ThrowExceptionForHR(_stream.Client.Start());

        _thread = new Thread(Pump) { IsBackground = true, Name = "LoopIt7 low latency capture" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void StopRecording()
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
        int blockAlign = _stream.Mix.BlockAlign;
        byte[] managed = new byte[Math.Max(_stream.BufferFrames, _stream.PeriodFrames * 4) * blockAlign];

        try
        {
            while (!_stopRequested)
            {
                LowLatencyInterop.WaitForSingleObject(_event, WaitTimeoutMs);
                if (_stopRequested) break;

                while (true)
                {
                    int hr = _capture.GetNextPacketSize(out uint packetFrames);
                    Marshal.ThrowExceptionForHR(hr);
                    if (packetFrames == 0) break;

                    Marshal.ThrowExceptionForHR(_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));

                    int bytes = (int)frames * blockAlign;
                    if (managed.Length < bytes) managed = new byte[bytes];

                    if ((flags & LowLatencyInterop.BufferflagsSilent) != 0 || data == IntPtr.Zero)
                    {
                        Array.Clear(managed, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, managed, 0, bytes);
                    }

                    _capture.ReleaseBuffer(frames);
                    CountPacket();
                    DataAvailable?.Invoke(this, new WaveInEventArgs(managed, bytes));
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            LowLatencyInterop.LeaveProAudio(proAudio);
            RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopRecording();
        try { Marshal.ReleaseComObject(_capture); } catch { }
        LowLatencyInterop.CloseHandle(_event);
        _stream.Dispose();
    }
}
