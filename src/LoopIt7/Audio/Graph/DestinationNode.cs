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
    private MMDevice? _device;
    private MixSampleProvider? _mixer;
    private SmoothGainSampleProvider? _gain;

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

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

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
        set { _muted = value; ApplyGain(); }
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
                error = StatusDetail;
                return false;
            }

            try
            {
                var mixFormat = _device.AudioClient.MixFormat;
                SampleRate = mixFormat.SampleRate;
                Channels = mixFormat.Channels;

                // The mixer works in stereo at the endpoint rate. Channel mapping is the very
                // last step, so a 7.1 receiver and a mono headset take the same summed pair.
                _mixer = new MixSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2));
                _gain = new SmoothGainSampleProvider(_mixer);
                ApplyGain();
                _gain.ResetToTarget();

                ISampleProvider chain = _gain;
                if (Channels != 2) chain = new ChannelMapSampleProvider(chain, Channels);

                RebuildMix();

                _player = new WasapiOut(_device, AudioClientShareMode.Shared, true, BufferMilliseconds);
                _player.PlaybackStopped += OnPlaybackStopped;
                _player.Init(chain);
                _player.Play();

                Status = NodeStatus.Live;
                StatusDetail = null;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                StopCore();
                Status = NodeStatus.Failed;
                StatusDetail = Describe(ex);
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
        if (gain is not null) gain.TargetGain = _muted ? 0f : _linearGain;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        Status = NodeStatus.Failed;
        StatusDetail = Describe(e.Exception);
        Failed?.Invoke(this, new GraphErrorEventArgs(Id, StatusDetail));
    }

    private void StopCore()
    {
        if (_player is not null)
        {
            _player.PlaybackStopped -= OnPlaybackStopped;
            try { _player.Stop(); } catch { }
            try { _player.Dispose(); } catch { }
            _player = null;
        }

        _mixer = null;
        _gain = null;

        if (_device is not null)
        {
            try { _device.Dispose(); } catch { }
            _device = null;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x88890004 => "In use in exclusive mode",
        System.Runtime.InteropServices.COMException com when (uint)com.HResult == 0x88890008 => "Format not accepted",
        System.Runtime.InteropServices.COMException => "Windows refused the device",
        _ => ex.Message
    };

    public void Dispose()
    {
        lock (_sync) StopCore();
    }
}
