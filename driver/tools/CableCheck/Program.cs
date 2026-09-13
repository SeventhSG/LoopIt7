// Round trip check for the LoopIt7 Cable driver.
//
// Plays a known test signal into "Cable In" and records "Cable Out", then measures what came
// back: level, which channel is which, clicks and dropouts, latency, and that a second
// playback session takes over the cable cleanly after the first one stops.
//
// The signal goes only to the cable's own playback endpoint, which is not a speaker. It could
// still reach one if something forwards Cable Out onward, so the check refuses to start while
// "Listen to this device" is on for Cable Out or LoopIt7 is running, and it never falls back
// to a default device: no LoopIt7 Cable endpoints, no sound.

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CableCheck;

internal static class Program
{
    private const string CableFamily = "LoopIt7 Cable";

    // Left and right get different tones so a swapped or collapsed pair is caught.
    private const double LeftHz = 1000.0;
    private const double RightHz = 1500.0;
    private const float Amplitude = 0.25f;                   // -12 dBFS peak
    private const double LeadInSeconds = 0.5;
    private const double ToneSeconds = 3.0;
    private const double GapSeconds = 1.5;                   // between the two sessions

    // "Listen to this device" on a recording endpoint: PKEY {24DBB0FC-...},1.
    private static readonly PropertyKey ListenKey = new(new Guid("24DBB0FC-9311-4B3D-9CF0-18FF155639D4"), 1);

    private static int Main(string[] args)
    {
        Console.WriteLine("LoopIt7 Cable round trip check");
        Console.WriteLine();

        if (args.Contains("--self-test"))
        {
            return SelfTest();
        }

        using var enumerator = new MMDeviceEnumerator();

        var cableIn = FindEndpoint(enumerator, DataFlow.Render, "Cable In");
        var cableOut = FindEndpoint(enumerator, DataFlow.Capture, "Cable Out");
        if (cableIn is null || cableOut is null)
        {
            Console.WriteLine("The LoopIt7 Cable endpoints are not present. Install the driver first.");
            Console.WriteLine("Nothing was played.");
            return 2;
        }

        Console.WriteLine($"  playing into    {cableIn.FriendlyName}");
        Console.WriteLine($"  recording from  {cableOut.FriendlyName}");
        Console.WriteLine($"  Cable In volume {cableIn.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}%{(cableIn.AudioEndpointVolume.Mute ? ", muted" : "")}");
        Console.WriteLine($"  Cable Out level {cableOut.AudioEndpointVolume.MasterVolumeLevelScalar * 100:0}%{(cableOut.AudioEndpointVolume.Mute ? ", muted" : "")}");
        Console.WriteLine();

        if (IsListening(cableOut))
        {
            Console.WriteLine("\"Listen to this device\" is on for Cable Out, so the test tone would reach your");
            Console.WriteLine("speakers. Turn it off (Sound settings > Cable Out > Properties > Listen) and run again.");
            Console.WriteLine("Nothing was played.");
            return 2;
        }

        if (Process.GetProcessesByName("LoopIt7").Length > 0)
        {
            Console.WriteLine("LoopIt7 is running. Its routing could carry Cable Out to your speakers, so close it");
            Console.WriteLine("(including the tray icon) and run again. Nothing was played.");
            return 2;
        }

        if (!args.Contains("--yes"))
        {
            Console.WriteLine("The tone goes only into the cable, not to a speaker. Turn your output down anyway.");
            Console.Write("Press Enter to start, or Ctrl+C to stop. ");
            Console.ReadLine();
            Console.WriteLine();
        }

        return Run(cableIn, cableOut);
    }

    private static MMDevice? FindEndpoint(MMDeviceEnumerator enumerator, DataFlow flow, string endpointName)
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            var name = device.FriendlyName;
            // The cable has one endpoint per direction, so the family name alone is enough.
            // Windows may label the playback end by its type ("Speakers") rather than
            // "Cable In", so the end's own name is not required to match.
            if (name.Contains(CableFamily, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }
        return null;
    }

    private static bool IsListening(MMDevice device)
    {
        try
        {
            var store = device.Properties;
            if (!store.Contains(ListenKey)) return false;
            return store[ListenKey].Value is bool on && on;
        }
        catch
        {
            // If it cannot be read, assume the worst.
            return true;
        }
    }

    private static int Run(MMDevice cableIn, MMDevice cableOut)
    {
        var recorded = new List<float>(48000 * 2 * 16);
        var recordLock = new object();

        using var capture = new WasapiCapture(cableOut, useEventSync: true, audioBufferMillisecondsLength: 20);
        var captureFormat = capture.WaveFormat;
        if (captureFormat.Encoding != WaveFormatEncoding.IeeeFloat && captureFormat.Encoding != WaveFormatEncoding.Extensible ||
            captureFormat.BitsPerSample != 32 || captureFormat.Channels != 2)
        {
            Console.WriteLine($"Unexpected Cable Out mix format: {captureFormat}. Expected 32-bit float stereo.");
            return 1;
        }
        int rate = captureFormat.SampleRate;

        capture.DataAvailable += (_, e) =>
        {
            lock (recordLock)
            {
                for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
                {
                    recorded.Add(BitConverter.ToSingle(e.Buffer, i));
                }
            }
        };

        capture.StartRecording();
        Thread.Sleep(700);                                   // idle: the cable must be silent

        var sessionStarts = new List<long>();
        for (int session = 0; session < 2; session++)
        {
            lock (recordLock) sessionStarts.Add(recorded.Count / 2);
            PlaySession(cableIn);
            Thread.Sleep(TimeSpan.FromSeconds(GapSeconds));
        }

        capture.StopRecording();

        float[] samples;
        lock (recordLock) samples = recorded.ToArray();

        return Analyse(samples, rate, sessionStarts);
    }

    private static void PlaySession(MMDevice cableIn)
    {
        var signal = new TestSignal(48000);
        using var output = new WasapiOut(cableIn, AudioClientShareMode.Shared, useEventSync: true, latency: 30);
        output.Init(signal);
        output.Play();
        Thread.Sleep(TimeSpan.FromSeconds(LeadInSeconds + ToneSeconds + 0.3));
        output.Stop();
    }

    private static int Analyse(float[] samples, int rate, List<long> sessionStarts)
    {
        int frames = samples.Length / 2;
        bool pass = true;

        void Report(bool ok, string text)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  ok    " : "  FAIL  ");
            Console.ResetColor();
            Console.WriteLine(text);
            pass &= ok;
        }

        Console.WriteLine($"recorded {frames / (double)rate:0.00} s at {rate} Hz");
        Console.WriteLine();

        // Idle: before anything plays, the cable must carry true silence. The sample driver this
        // is built on used to synthesise a tone on its recording side; none of that may leak.
        {
            int idleEnd = (int)Math.Min(sessionStarts[0], frames);
            double peak = 0;
            for (int i = 0; i < idleEnd * 2; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
            Report(peak < 1e-4, $"idle cable is silent (peak {ToDb(peak):0.0} dBFS over {idleEnd / (double)rate:0.00} s)");
        }

        double expectedRms = Amplitude / Math.Sqrt(2);
        long searchFrom = sessionStarts[0];

        for (int session = 0; session < sessionStarts.Count; session++)
        {
            Console.WriteLine();
            Console.WriteLine($"session {session + 1}");

            long onset = -1;
            for (long i = Math.Max(searchFrom, sessionStarts[session]); i < frames; i++)
            {
                if (Math.Abs(samples[i * 2]) > 0.05f || Math.Abs(samples[i * 2 + 1]) > 0.05f)
                {
                    onset = i;
                    break;
                }
            }

            if (onset < 0)
            {
                Report(false, "the tone never came out of Cable Out");
                continue;
            }

            double latencyMs = (onset - sessionStarts[session]) / (double)rate * 1000.0 - LeadInSeconds * 1000.0;
            Console.WriteLine($"  info  tone arrived {latencyMs:0} ms after it was due (approximate: includes the");
            Console.WriteLine("        playback buffer and the cable's cushion, measured from when playback started)");

            // Analyse the steady middle of the tone, away from its start and end.
            long from = onset + (long)(0.3 * rate);
            long to = onset + (long)((ToneSeconds - 0.3) * rate);
            if (to > frames)
            {
                Report(false, "the recording ends before the tone does");
                continue;
            }

            for (int channel = 0; channel < 2; channel++)
            {
                double hz = channel == 0 ? LeftHz : RightHz;
                string side = channel == 0 ? "left " : "right";

                double rms = Rms(samples, channel, from, to);
                // Rounded, and + 0.0 turns a negative zero positive, so it never prints "-+0.00".
                double levelDb = Math.Round(ToDb(rms) - ToDb(expectedRms), 2) + 0.0;
                Report(Math.Abs(levelDb) < 0.5, $"{side} level {ToDb(rms):0.00} dBFS rms, {levelDb:+0.00;-0.00} dB from what was sent");

                double measuredHz = ZeroCrossingHz(samples, channel, from, to, rate);
                Report(Math.Abs(measuredHz - hz) < hz * 0.01, $"{side} carries {measuredHz:0.0} Hz (sent {hz:0} Hz)");

                int glitches = CountGlitches(samples, channel, from, to, hz, rate);
                Report(glitches == 0, $"{side} has {glitches} click{(glitches == 1 ? "" : "s")} or dropout{(glitches == 1 ? "" : "s")} in {(to - from) / (double)rate:0.0} s");
            }

            searchFrom = onset + (long)(ToneSeconds * rate);
        }

        Console.WriteLine();
        if (pass)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS: what goes into Cable In comes out of Cable Out, intact.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAIL: details above.");
        }
        Console.ResetColor();
        return pass ? 0 : 1;
    }

    private static double Rms(float[] s, int channel, long from, long to)
    {
        double sum = 0;
        for (long i = from; i < to; i++) sum += s[i * 2 + channel] * (double)s[i * 2 + channel];
        return Math.Sqrt(sum / (to - from));
    }

    private static double ZeroCrossingHz(float[] s, int channel, long from, long to, int rate)
    {
        long first = -1, last = -1;
        int crossings = 0;
        for (long i = from + 1; i < to; i++)
        {
            if (s[(i - 1) * 2 + channel] < 0 && s[i * 2 + channel] >= 0)
            {
                if (first < 0) first = i; else crossings++;
                last = i;
            }
        }
        return crossings > 0 ? crossings * (double)rate / (last - first) : 0;
    }

    // A pure sine obeys x[n+1] = 2cos(w)x[n] - x[n-1] exactly. What is left over is only
    // 16-bit rounding (well under 0.001 here); a dropped, repeated or zeroed stretch of samples
    // leaves a residual orders of magnitude larger. Each run of large residuals is one event.
    private static int CountGlitches(float[] s, int channel, long from, long to, double hz, int rate)
    {
        double k = 2 * Math.Cos(2 * Math.PI * hz / rate);
        const double threshold = 0.005;
        int events = 0;
        bool inEvent = false;
        long quietUntil = 0;

        for (long i = from + 1; i < to - 1; i++)
        {
            double residual = s[(i + 1) * 2 + channel] - k * s[i * 2 + channel] + s[(i - 1) * 2 + channel];
            if (Math.Abs(residual) > threshold)
            {
                if (!inEvent && i >= quietUntil) events++;
                inEvent = true;
                quietUntil = i + 8;
            }
            else
            {
                inEvent = false;
            }
        }
        return events;
    }

    private static double ToDb(double linear) => linear <= 0 ? -200 : 20 * Math.Log10(linear);

    // Checks the analysis itself, with no audio device involved: a clean synthetic recording
    // must pass, and the same recording with a few samples dropped, or its channels swapped,
    // must fail. Quantised to 16 bits like the cable, so rounding alone must not count as a click.
    private static int SelfTest()
    {
        const int rate = 48000;

        float[] Build(bool dropSamples, bool swap)
        {
            var frames = new List<(float L, float R)>();
            void Silence(double seconds) { for (int i = 0; i < seconds * rate; i++) frames.Add((0, 0)); }
            float Q(double v) => (float)(Math.Round(v * 32767) / 32767);

            Silence(0.7);
            for (int session = 0; session < 2; session++)
            {
                Silence(LeadInSeconds + 0.040);                // 40 ms of simulated latency
                int n = (int)(ToneSeconds * rate);
                for (int i = 0; i < n; i++)
                {
                    if (dropSamples && session == 1 && i >= rate && i < rate + 12) continue;
                    double t = i / (double)rate;
                    float l = Q(Amplitude * Math.Sin(2 * Math.PI * LeftHz * t));
                    float r = Q(Amplitude * Math.Sin(2 * Math.PI * RightHz * t));
                    frames.Add(swap ? (r, l) : (l, r));
                }
                Silence(GapSeconds);
            }
            return frames.SelectMany(f => new[] { f.L, f.R }).ToArray();
        }

        int sessionFrames = (int)((LeadInSeconds + 0.040 + ToneSeconds + GapSeconds) * rate);
        var starts = new List<long> { (long)(0.7 * rate), (long)(0.7 * rate) + sessionFrames };

        Console.WriteLine("=== clean recording, must PASS");
        bool clean = Analyse(Build(false, false), rate, starts) == 0;
        Console.WriteLine();
        Console.WriteLine("=== 12 samples dropped in session 2, must FAIL");
        bool dropped = Analyse(Build(true, false), rate, starts) != 0;
        Console.WriteLine();
        Console.WriteLine("=== channels swapped, must FAIL");
        bool swapped = Analyse(Build(false, true), rate, starts) != 0;

        Console.WriteLine();
        bool ok = clean && dropped && swapped;
        Console.WriteLine(ok ? "self-test: the analysis catches what it should" : "self-test: the analysis is wrong");
        return ok ? 0 : 1;
    }

    /// <summary>A lead-in of silence, then left and right tones, then silence.</summary>
    private sealed class TestSignal : ISampleProvider
    {
        private readonly int _rate;
        private long _frame;

        public TestSignal(int rate)
        {
            _rate = rate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            long toneStart = (long)(LeadInSeconds * _rate);
            long toneEnd = toneStart + (long)(ToneSeconds * _rate);

            for (int i = 0; i + 1 < count; i += 2, _frame++)
            {
                float left = 0, right = 0;
                if (_frame >= toneStart && _frame < toneEnd)
                {
                    double t = (_frame - toneStart) / (double)_rate;
                    left = (float)(Amplitude * Math.Sin(2 * Math.PI * LeftHz * t));
                    right = (float)(Amplitude * Math.Sin(2 * Math.PI * RightHz * t));
                }
                buffer[offset + i] = left;
                buffer[offset + i + 1] = right;
            }
            return count;
        }
    }
}
