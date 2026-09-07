using NAudio.Wave;

namespace LoopIt7.Audio.Graph;

/// <summary>
/// Sums every cable arriving at one destination. NAudio ships a mixer, but it drops inputs
/// that report end of stream and it cannot be re-stocked from another thread while running,
/// which is exactly what a patchbay needs to do.
/// </summary>
internal sealed class MixSampleProvider : ISampleProvider
{
    private readonly object _sync = new();
    private ISampleProvider[] _inputs = [];
    private float[] _scratch = [];
    private volatile float _peak;

    public MixSampleProvider(WaveFormat format)
    {
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Replaces the input set. Safe to call while the render thread is pulling.</summary>
    public void SetInputs(IEnumerable<ISampleProvider> inputs)
    {
        var array = inputs.ToArray();
        foreach (var input in array)
        {
            if (input.WaveFormat.SampleRate != WaveFormat.SampleRate ||
                input.WaveFormat.Channels != WaveFormat.Channels)
            {
                throw new ArgumentException("Every cable must reach the mixer in the destination's format.");
            }
        }

        lock (_sync) _inputs = array;
    }

    /// <summary>Peak of the summed output since the last read, then resets.</summary>
    public float ReadPeak()
    {
        float peak = _peak;
        _peak = 0f;
        return peak;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ISampleProvider[] inputs;
        lock (_sync) inputs = _inputs;

        Array.Clear(buffer, offset, count);

        if (inputs.Length > 0)
        {
            if (_scratch.Length < count) _scratch = new float[count];

            for (int i = 0; i < inputs.Length; i++)
            {
                int read = inputs[i].Read(_scratch, 0, count);
                for (int s = 0; s < read; s++)
                {
                    buffer[offset + s] += _scratch[s];
                }
            }
        }

        float peak = _peak;
        for (int s = 0; s < count; s++)
        {
            float magnitude = Math.Abs(buffer[offset + s]);
            if (magnitude > peak) peak = magnitude;
        }

        _peak = peak;

        // Always hand back a full block. A destination with nothing patched into it plays
        // silence rather than reporting the end of its stream and stopping.
        return count;
    }
}
