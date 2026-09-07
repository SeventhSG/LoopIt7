using NAudio.Wave;

namespace LoopIt7.Audio;

/// <summary>
/// Maps the stereo bus onto whatever channel count the destination endpoint runs at.
/// Mono endpoints get a downmix, multichannel endpoints get the pair on the front two
/// channels and silence elsewhere, which is what a stereo source should do.
/// </summary>
public sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private readonly int _targetChannels;
    private float[] _scratch = [];

    public ChannelMapSampleProvider(ISampleProvider source, int targetChannels)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        _targetChannels = targetChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, targetChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _targetChannels;
        int needed = frames * _sourceChannels;

        if (_scratch.Length < needed) _scratch = new float[needed];

        int read = _source.Read(_scratch, 0, needed);
        int readFrames = read / _sourceChannels;

        for (int f = 0; f < readFrames; f++)
        {
            int src = f * _sourceChannels;
            int dst = offset + f * _targetChannels;

            if (_targetChannels == 1)
            {
                float sum = 0f;
                for (int c = 0; c < _sourceChannels; c++) sum += _scratch[src + c];
                buffer[dst] = sum / _sourceChannels;
                continue;
            }

            for (int c = 0; c < _targetChannels; c++)
            {
                buffer[dst + c] = c < _sourceChannels ? _scratch[src + c] : 0f;
            }
        }

        return readFrames * _targetChannels;
    }
}
