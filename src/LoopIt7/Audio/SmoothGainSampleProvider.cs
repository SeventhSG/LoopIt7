using NAudio.Wave;

namespace LoopIt7.Audio;

/// <summary>
/// Applies gain with a per-sample ramp toward the target, and reports the peak it saw.
/// The ramp is the point: a hard multiply changes amplitude between two buffers and you
/// hear that step as a click every time the slider moves.
/// </summary>
public sealed class SmoothGainSampleProvider : ISampleProvider
{
    private const float RampSeconds = 0.020f;

    private readonly ISampleProvider _source;
    private readonly float _step;

    private float _current = 1f;
    private volatile float _target = 1f;
    private volatile float _peak;

    public SmoothGainSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;

        // One ramp step per frame, sized so a full 0 to 1 sweep takes RampSeconds.
        _step = 1f / Math.Max(1f, WaveFormat.SampleRate * RampSeconds);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Linear gain. Set from the UI thread at any time.</summary>
    public float TargetGain
    {
        get => _target;
        set => _target = Math.Clamp(value, 0f, 8f);
    }

    /// <summary>Snaps the ramp to the target. Use when starting, so playback does not fade in twice.</summary>
    public void ResetToTarget() => _current = _target;

    /// <summary>
    /// Highest absolute sample since the last read of this property, then resets.
    /// Reading is destructive so the meter always shows a fresh window.
    /// </summary>
    public float ReadPeak()
    {
        float peak = _peak;
        _peak = 0f;
        return peak;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        if (read == 0) return 0;

        int channels = WaveFormat.Channels;
        float target = _target;
        float gain = _current;
        float step = _step;
        float peak = _peak;

        for (int i = 0; i < read; i += channels)
        {
            if (gain < target)
            {
                gain = Math.Min(target, gain + step);
            }
            else if (gain > target)
            {
                gain = Math.Max(target, gain - step);
            }

            for (int c = 0; c < channels && i + c < read; c++)
            {
                float sample = buffer[offset + i + c] * gain;
                buffer[offset + i + c] = sample;

                float magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;
            }
        }

        _current = gain;
        _peak = peak;
        return read;
    }
}
