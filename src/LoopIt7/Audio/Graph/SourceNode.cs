using System.Diagnostics;
using LoopIt7.Audio.Interop;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LoopIt7.Audio.Graph;

/// <summary>
/// Anything that produces audio into the patchbay. Whatever the capture underneath is, every
/// source hands the graph the same thing: interleaved stereo float at its own sample rate.
/// </summary>
internal abstract class SourceNode : IDisposable
{
    private volatile Connection[] _connections = [];
    private float[] _stereo = [];
    private byte[] _stereoBytes = [];
    private float _gainCurrent = 1f;
    private volatile float _gainTarget = 1f;
    private volatile float _peak;
    private float _rampStep = 0.001f;

    protected SourceNode(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string DisplayName { get; protected set; }

    public abstract SourceKind Kind { get; }

    public NodeStatus Status { get; protected set; } = NodeStatus.Idle;
    public string? StatusDetail { get; protected set; }

    /// <summary>Stereo float format the cables leaving this node are written in.</summary>
    public WaveFormat StereoFormat { get; protected set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    /// <summary>Channel count of the underlying capture, before it is folded to stereo.</summary>
    public int NativeChannels { get; protected set; }

    /// <summary>Milliseconds of audio the cables are allowed to queue before they get trimmed.</summary>
    public int TargetQueueMilliseconds { get; set; } = 30;

    public float LinearGain
    {
        get => _gainTarget;
        set => _gainTarget = Math.Clamp(value, 0f, 8f);
    }

    public bool Muted { get; set; }

    public float ReadPeak()
    {
        float peak = _peak;
        _peak = 0f;
        return peak;
    }

    /// <summary>Raised off the UI thread when the capture dies on its own.</summary>
    public event EventHandler<GraphErrorEventArgs>? Failed;

    public void SetConnections(IEnumerable<Connection> connections) => _connections = connections.ToArray();

    public abstract bool Start(out string? error);
    public abstract void Stop();

    public void MarkWaiting(string detail)
    {
        Stop();
        Status = NodeStatus.Waiting;
        StatusDetail = detail;
    }

    protected void PrepareBus(WaveFormat stereoFormat)
    {
        StereoFormat = stereoFormat;
        _rampStep = 1f / Math.Max(1f, stereoFormat.SampleRate * 0.020f);
        _gainCurrent = Muted ? 0f : _gainTarget;
    }

    protected void RaiseFailure(string message)
    {
        Status = NodeStatus.Failed;
        StatusDetail = message;
        Failed?.Invoke(this, new GraphErrorEventArgs(Id, message));
    }

    /// <summary>
    /// Converts one capture buffer to stereo float, applies the node fader, and pushes it
    /// down every cable leaving this node. Runs on the capture thread.
    /// </summary>
    protected void Distribute(byte[] source, int bytesRecorded, WaveFormat captureFormat)
    {
        int frames = bytesRecorded / captureFormat.BlockAlign;
        if (frames == 0) return;

        int samples = frames * 2;
        if (_stereo.Length < samples) _stereo = new float[samples];
        if (_stereoBytes.Length < samples * 4) _stereoBytes = new byte[samples * 4];

        Deinterleave(source, frames, captureFormat, _stereo);
        ApplyFader(_stereo, samples);

        Buffer.BlockCopy(_stereo, 0, _stereoBytes, 0, samples * 4);

        var connections = _connections;
        for (int i = 0; i < connections.Length; i++)
        {
            connections[i].Write(_stereoBytes, samples * 4, TargetQueueMilliseconds);
        }
    }

    private void ApplyFader(float[] bus, int sampleCount)
    {
        float target = Muted ? 0f : _gainTarget;
        float gain = _gainCurrent;
        float step = _rampStep;
        float peak = _peak;

        for (int i = 0; i < sampleCount; i += 2)
        {
            if (gain < target) gain = Math.Min(target, gain + step);
            else if (gain > target) gain = Math.Max(target, gain - step);

            float left = bus[i] * gain;
            float right = bus[i + 1] * gain;
            bus[i] = left;
            bus[i + 1] = right;

            float magnitude = Math.Max(Math.Abs(left), Math.Abs(right));
            if (magnitude > peak) peak = magnitude;
        }

        _gainCurrent = gain;
        _peak = peak;
    }

    private static void Deinterleave(byte[] source, int frames, WaveFormat format, float[] destination)
    {
        int channels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;

        for (int f = 0; f < frames; f++)
        {
            int offset = f * format.BlockAlign;
            float left = ReadSample(source, offset, bytesPerSample, isFloat);
            float right = channels > 1
                ? ReadSample(source, offset + bytesPerSample, bytesPerSample, isFloat)
                : left;

            destination[f * 2] = left;
            destination[f * 2 + 1] = right;
        }
    }

    private static float ReadSample(byte[] source, int offset, int bytesPerSample, bool isFloat)
    {
        if (isFloat) return BitConverter.ToSingle(source, offset);

        return bytesPerSample switch
        {
            2 => BitConverter.ToInt16(source, offset) / 32768f,
            3 => ((source[offset + 2] << 24 | source[offset + 1] << 16 | source[offset] << 8) >> 8) / 8388608f,
            4 => BitConverter.ToInt32(source, offset) / 2147483648f,
            1 => (source[offset] - 128) / 128f,
            _ => 0f
        };
    }

    protected static WaveFormat Normalize(WaveFormat format) =>
        format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;

    public virtual void Dispose() => Stop();
}

/// <summary>A microphone, a line input, or a playback endpoint tapped in loopback.</summary>
internal sealed class DeviceSourceNode : SourceNode
{
    private readonly DeviceService _devices;
    private readonly bool _loopback;

    private IWaveIn? _capture;
    private WaveFormat? _captureFormat;
    private MMDevice? _device;

    public DeviceSourceNode(string id, string displayName, string deviceId, bool loopback, DeviceService devices)
        : base(id, displayName)
    {
        DeviceId = deviceId;
        _loopback = loopback;
        _devices = devices;
    }

    public string DeviceId { get; }

    public override SourceKind Kind => _loopback ? SourceKind.DeviceLoopback : SourceKind.Device;

    /// <summary>Capture buffer in milliseconds. Only honoured for real recording endpoints.</summary>
    public int BufferMilliseconds { get; set; } = 10;

    public override bool Start(out string? error)
    {
        Stop();

        _device = _devices.TryGetDevice(DeviceId);
        if (_device is null)
        {
            Status = NodeStatus.Waiting;
            StatusDetail = "Waiting for this device";
            error = StatusDetail;
            return false;
        }

        try
        {
            _capture = _loopback
                ? new WasapiLoopbackCapture(_device)
                : new WasapiCapture(_device, true, BufferMilliseconds);

            _captureFormat = Normalize(_capture.WaveFormat);
            NativeChannels = _captureFormat.Channels;
            PrepareBus(WaveFormat.CreateIeeeFloatWaveFormat(_captureFormat.SampleRate, 2));

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            Status = NodeStatus.Live;
            StatusDetail = null;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Stop();
            Status = NodeStatus.Failed;
            StatusDetail = Describe(ex);
            error = StatusDetail;
            return false;
        }
    }

    public override void Stop()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }

        if (_device is not null)
        {
            try { _device.Dispose(); } catch { }
            _device = null;
        }

        _captureFormat = null;
        if (Status == NodeStatus.Live) Status = NodeStatus.Idle;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_captureFormat is null || e.BytesRecorded == 0) return;
        Distribute(e.Buffer, e.BytesRecorded, _captureFormat);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        RaiseFailure(Describe(e.Exception));
    }

    private static string Describe(Exception ex) => ex switch
    {
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x88890004 => "In use in exclusive mode",
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x88890008 => "Format not accepted",
        System.Runtime.InteropServices.COMException => "Windows refused the device",
        _ => ex.Message
    };
}

/// <summary>
/// One application and everything it spawned, captured on its own. No virtual cable in the
/// middle: Windows hands us that program's mix directly.
/// </summary>
internal sealed class AppSourceNode : SourceNode
{
    /// <summary>Process capture lets us name the format, so we ask for the one we want.</summary>
    private static readonly WaveFormat CaptureFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    private ProcessLoopbackCapture? _capture;

    public AppSourceNode(string id, string displayName, int processId, string executableName)
        : base(id, displayName)
    {
        ProcessId = processId;
        ExecutableName = executableName;
    }

    public int ProcessId { get; private set; }

    /// <summary>Used to find the program again after it restarts under a new process id.</summary>
    public string ExecutableName { get; }

    public override SourceKind Kind => SourceKind.Application;

    public override bool Start(out string? error)
    {
        Stop();

        if (!ProcessLoopbackCapture.IsSupported)
        {
            Status = NodeStatus.Failed;
            StatusDetail = "This build of Windows cannot capture single applications";
            error = StatusDetail;
            return false;
        }

        if (!TryResolveProcess())
        {
            Status = NodeStatus.Waiting;
            StatusDetail = $"Waiting for {ExecutableName} to start";
            error = StatusDetail;
            return false;
        }

        try
        {
            NativeChannels = CaptureFormat.Channels;
            PrepareBus(CaptureFormat);

            _capture = new ProcessLoopbackCapture(ProcessId, CaptureFormat);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            Status = NodeStatus.Live;
            StatusDetail = null;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            Stop();
            Status = NodeStatus.Failed;
            StatusDetail = ex.Message;
            error = StatusDetail;
            return false;
        }
    }

    public override void Stop()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { }
            _capture = null;
        }

        if (Status == NodeStatus.Live) Status = NodeStatus.Idle;
    }

    /// <summary>True when the program is running, updating the process id if it restarted.</summary>
    public bool TryResolveProcess()
    {
        try
        {
            using var existing = Process.GetProcessById(ProcessId);
            if (!existing.HasExited) return true;
        }
        catch
        {
            // Fall through and look the program up by name instead.
        }

        var candidates = Process.GetProcessesByName(ExecutableName);
        try
        {
            var replacement = candidates.FirstOrDefault(p => !p.HasExited);
            if (replacement is null) return false;

            ProcessId = replacement.Id;
            return true;
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        Distribute(e.Buffer, e.BytesRecorded, CaptureFormat);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        RaiseFailure(e.Exception.Message);
    }
}
