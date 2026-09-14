using NAudio.Wave;

namespace LoopIt7.Audio;

/// <summary>
/// The last thing before an output: catches a feedback loop that runs through another program.
/// <para>
/// The feedback guard refuses loops it can see, but some go through somebody else's software.
/// When VoiceMeeter sends a bus into LoopIt7 Cable and LoopIt7 sends that cable into VoiceMeeter,
/// the sound goes round at unity gain, every new sound piles on top, and the level climbs in
/// whatever the user is wearing. Nothing inside LoopIt7 can see that wire, but it can hear it.
/// </para>
/// <para>
/// First line: a dip. A level that keeps climbing, window after window, is what a loop does and
/// what music almost never does for this long. The output fades to silence for long enough
/// that everything in flight round the loop drains away, then fades back in. That is what
/// pulling a fader down and back up does by hand, and it is what clears the fizz.
/// </para>
/// <para>
/// Second line: a mute. If it has to dip again and again, or the level sits pinned at full
/// scale, the loop is not going away on its own, and the output stays silent until the user
/// unmutes it. Restarting on its own would only start the loop again.
/// </para>
/// </summary>
public sealed class RunawayGuardSampleProvider : ISampleProvider
{
    /// <summary>How long the level has to stay pinned before the output is silenced.</summary>
    public const int TripMilliseconds = 500;

    /// <summary>A window counts as pinned when it peaks at full scale and is loud throughout.</summary>
    public const float PeakThreshold = 0.98f;
    public const float RmsThreshold = 0.6f;

    /// <summary>Building: every 50 ms window at least this much louder than the one before...</summary>
    public const float GrowthPerWindow = 1.02f;

    /// <summary>
    /// ...for three seconds without one window falling back. Fizz building round a loop keeps
    /// that up; music, with its beats and notes, falls back, and even a smooth swell loses its
    /// percentage growth long before three seconds are out. A shorter window caught a plain
    /// four second swell of a tone in the tests.
    /// </summary>
    public const int GrowthWindows = 60;

    /// <summary>...and loud enough by then to matter.</summary>
    public const float BuildingRms = 0.25f;

    /// <summary>How long the dip holds silence, longer than any round trip through a loop.</summary>
    public const int DipHoldMilliseconds = 200;

    /// <summary>This many dips inside <see cref="DipWindowSeconds"/> means the loop keeps coming back.</summary>
    public const int DipsBeforeMute = 4;
    public const int DipWindowSeconds = 15;

    private const float FadeOutSeconds = 0.010f;
    private const float FadeInSeconds = 0.100f;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly int _pinWindowFrames;
    private readonly int _tripWindows;
    private readonly int _growthWindowFrames;
    private readonly int _fadeOutFrames;
    private readonly int _holdFrames;
    private readonly int _fadeInFrames;

    // Pinned at full scale, in 10 ms windows.
    private int _pinFrames;
    private float _pinPeak;
    private double _pinSquares;
    private int _pinnedWindows;

    // Building, in 50 ms windows.
    private int _growthFrames;
    private double _growthSquares;
    private double _previousRms;
    private int _risingWindows;

    // The dip in progress: frames left in each phase.
    private int _dipFadeOut;
    private int _dipHold;
    private int _dipFadeIn;
    private long _framesSeen;
    private readonly Queue<long> _recentDips = new();

    private int _muteFade;
    private volatile bool _tripped;

    public RunawayGuardSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _channels = WaveFormat.Channels;

        int rate = WaveFormat.SampleRate;
        _pinWindowFrames = Math.Max(1, rate / 100);
        _tripWindows = TripMilliseconds / 10;
        _growthWindowFrames = Math.Max(1, rate / 20);
        _fadeOutFrames = Math.Max(1, (int)(rate * FadeOutSeconds));
        _holdFrames = rate * DipHoldMilliseconds / 1000;
        _fadeInFrames = Math.Max(1, (int)(rate * FadeInSeconds));
    }

    public WaveFormat WaveFormat { get; }

    public bool Tripped => _tripped;

    /// <summary>Dips since the guard was built or rearmed.</summary>
    public int Dips { get; private set; }

    /// <summary>Raised once when the output is silenced for good, on the render thread.</summary>
    public event EventHandler? Runaway;

    /// <summary>Raised on every automatic dip, on the render thread.</summary>
    public event EventHandler? Dipped;

    /// <summary>Lets sound through again. Called when the user unmutes the output.</summary>
    public void Rearm()
    {
        _pinnedWindows = 0;
        _pinFrames = 0;
        _pinPeak = 0f;
        _pinSquares = 0;
        _risingWindows = 0;
        _growthFrames = 0;
        _growthSquares = 0;
        _previousRms = 0;
        _dipFadeOut = _dipHold = _dipFadeIn = 0;
        _recentDips.Clear();
        _tripped = false;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        for (int i = 0; i < read; i += _channels)
        {
            _framesSeen++;

            if (_tripped)
            {
                float fade = _muteFade > 0 ? _muteFade-- / (float)_fadeOutFrames : 0f;
                Scale(buffer, offset + i, read - i, fade);
                continue;
            }

            if (InDip(out float dipGain))
            {
                Scale(buffer, offset + i, read - i, dipGain);
                continue;
            }

            Measure(buffer, offset + i, read - i);
        }

        return read;
    }

    /// <summary>Advances the dip by one frame and reports its gain. False when there is none.</summary>
    private bool InDip(out float gain)
    {
        if (_dipFadeOut > 0)
        {
            gain = --_dipFadeOut / (float)_fadeOutFrames;
            return true;
        }

        if (_dipHold > 0)
        {
            _dipHold--;
            gain = 0f;
            return true;
        }

        if (_dipFadeIn > 0)
        {
            gain = 1f - --_dipFadeIn / (float)_fadeInFrames;
            return true;
        }

        gain = 1f;
        return false;
    }

    private void Measure(float[] buffer, int index, int remaining)
    {
        for (int c = 0; c < _channels && c < remaining; c++)
        {
            float sample = buffer[index + c];
            float magnitude = Math.Abs(sample);
            if (magnitude > _pinPeak) _pinPeak = magnitude;
            _pinSquares += sample * sample;
            _growthSquares += sample * sample;
        }

        if (++_pinFrames >= _pinWindowFrames)
        {
            double rms = Math.Sqrt(_pinSquares / (_pinWindowFrames * _channels));
            _pinnedWindows = _pinPeak >= PeakThreshold && rms >= RmsThreshold ? _pinnedWindows + 1 : 0;
            _pinFrames = 0;
            _pinPeak = 0f;
            _pinSquares = 0;

            if (_pinnedWindows >= _tripWindows)
            {
                Trip();
                return;
            }
        }

        if (++_growthFrames >= _growthWindowFrames)
        {
            double rms = Math.Sqrt(_growthSquares / (_growthWindowFrames * _channels));
            bool rising = _previousRms > 0 && rms >= _previousRms * GrowthPerWindow;
            _risingWindows = rising ? _risingWindows + 1 : 0;
            _previousRms = rms;
            _growthFrames = 0;
            _growthSquares = 0;

            if (_risingWindows >= GrowthWindows && rms >= BuildingRms) Dip();
        }
    }

    private void Dip()
    {
        _risingWindows = 0;
        _previousRms = 0;

        // Dips close together mean the loop refills as fast as it is drained.
        long window = (long)WaveFormat.SampleRate * DipWindowSeconds;
        while (_recentDips.Count > 0 && _framesSeen - _recentDips.Peek() > window) _recentDips.Dequeue();
        _recentDips.Enqueue(_framesSeen);

        if (_recentDips.Count >= DipsBeforeMute)
        {
            Trip();
            return;
        }

        Dips++;
        _dipFadeOut = _fadeOutFrames;
        _dipHold = _holdFrames;
        _dipFadeIn = _fadeInFrames;
        Dipped?.Invoke(this, EventArgs.Empty);
    }

    private void Trip()
    {
        _tripped = true;
        _muteFade = _fadeOutFrames;
        Runaway?.Invoke(this, EventArgs.Empty);
    }

    private void Scale(float[] buffer, int index, int remaining, float gain)
    {
        for (int c = 0; c < _channels && c < remaining; c++) buffer[index + c] *= gain;
    }
}
