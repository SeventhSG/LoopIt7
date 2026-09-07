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
    private volatile float _pan;
    private float _panCurrent;

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

    /// <summary>
    /// Stereo balance from -1 (hard left) to +1 (hard right). A balance law rather than a
    /// constant power pan: the material here is already stereo, so the correct move is to
    /// turn one side down, never to boost the other above unity.
    /// </summary>
    public float Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1f, 1f);
    }

    /// <summary>Snaps the ramps to their targets. Use when starting, so nothing fades in twice.</summary>
    public void ResetToTarget()
    {
        _current = _target;
        _panCurrent = _pan;
    }

    internal static void BalanceGains(float pan, out float left, out float right)
    {
        left = pan > 0 ? 1f - pan : 1f;
        right = pan < 0 ? 1f + pan : 1f;
    }

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
        float panTarget = _pan;
        float pan = _panCurrent;
        float step = _step;
        float peak = _peak;
        bool stereo = channels == 2;

        for (int i = 0; i < read; i += channels)
        {
            if (gain < target) gain = Math.Min(target, gain + step);
            else if (gain > target) gain = Math.Max(target, gain - step);

            // Pan rides the same ramp as the fader, so dragging it never clicks either.
            if (pan < panTarget) pan = Math.Min(panTarget, pan + step);
            else if (pan > panTarget) pan = Math.Max(panTarget, pan - step);

            float leftTrim = 1f;
            float rightTrim = 1f;
            if (stereo && pan != 0f) BalanceGains(pan, out leftTrim, out rightTrim);

            for (int c = 0; c < channels && i + c < read; c++)
            {
                float trim = stereo ? (c == 0 ? leftTrim : rightTrim) : 1f;
                float sample = buffer[offset + i + c] * gain * trim;
                buffer[offset + i + c] = sample;

                float magnitude = Math.Abs(sample);
                if (magnitude > peak) peak = magnitude;
            }
        }

        _current = gain;
        _panCurrent = pan;
        _peak = peak;
        return read;
    }
}
