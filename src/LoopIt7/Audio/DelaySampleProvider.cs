using NAudio.Wave;

namespace LoopIt7.Audio;

/// <summary>
/// A fixed delay line, used to time align one output against another. Speakers three
/// metres further away need about 9 ms; a wireless in ear pack usually needs the room
/// monitor pushed back to meet it.
/// </summary>
public sealed class DelaySampleProvider : ISampleProvider
{
    /// <summary>Upper bound for the ring buffer, and for what the UI may ask for.</summary>
    public const int MaxDelayMilliseconds = 500;

    private const float FadeSeconds = 0.010f;

    private readonly ISampleProvider _source;
    private readonly float[] _ring;
    private readonly int _frameCapacity;
    private readonly int _channels;
    private readonly int _fadeFrames;

    private int _writeFrame;
    private int _delayFrames;
    private volatile int _requestedDelayFrames;
    private int _fadeRemaining;

    public DelaySampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = WaveFormat.Channels;

        // +1 frame so a full length delay never has the read head land on the write head.
        _frameCapacity = (int)(WaveFormat.SampleRate * (MaxDelayMilliseconds / 1000.0)) + 1;
        _ring = new float[_frameCapacity * _channels];
        _fadeFrames = Math.Max(1, (int)(WaveFormat.SampleRate * FadeSeconds));
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Delay in milliseconds, clamped to <see cref="MaxDelayMilliseconds"/>.</summary>
    public int DelayMilliseconds
    {
        get => (int)Math.Round(_requestedDelayFrames * 1000.0 / WaveFormat.SampleRate);
        set
        {
            int ms = Math.Clamp(value, 0, MaxDelayMilliseconds);
            _requestedDelayFrames = (int)(WaveFormat.SampleRate * (ms / 1000.0));
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0) return 0;

        int requested = _requestedDelayFrames;
        if (requested != _delayFrames)
        {
            // Moving the read head mid stream is a discontinuity. Fade across it rather
            // than let a click through the monitors.
            _delayFrames = requested;
            _fadeRemaining = _fadeFrames;
        }

        if (_delayFrames == 0 && _fadeRemaining == 0)
        {
            // Still keep the ring primed so a later delay change has history to read from.
            WriteToRing(buffer, offset, read);
            return read;
        }

        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            int writeIndex = _writeFrame * _channels;
            int readFrame = _writeFrame - _delayFrames;
            if (readFrame < 0) readFrame += _frameCapacity;
            int readIndex = readFrame * _channels;

            float fade = 1f;
            if (_fadeRemaining > 0)
            {
                fade = 1f - (_fadeRemaining / (float)_fadeFrames);
                _fadeRemaining--;
            }

            for (int c = 0; c < _channels; c++)
            {
                float incoming = buffer[offset + f * _channels + c];
                _ring[writeIndex + c] = incoming;
                buffer[offset + f * _channels + c] = _ring[readIndex + c] * fade;
            }

            _writeFrame++;
            if (_writeFrame >= _frameCapacity) _writeFrame = 0;
        }

        return read;
    }

    private void WriteToRing(float[] buffer, int offset, int read)
    {
        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            int writeIndex = _writeFrame * _channels;
            for (int c = 0; c < _channels; c++)
            {
                _ring[writeIndex + c] = buffer[offset + f * _channels + c];
            }

            _writeFrame++;
            if (_writeFrame >= _frameCapacity) _writeFrame = 0;
        }
    }
}
