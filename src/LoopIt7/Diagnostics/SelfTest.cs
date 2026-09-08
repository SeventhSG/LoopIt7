using System.Diagnostics;
using System.Text;
using LoopIt7.Audio.Graph;
using LoopIt7.Audio.Interop;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LoopIt7.Diagnostics;

/// <summary>
/// Headless checks, reachable with <c>LoopIt7.exe --self-test</c>. Audio stacks differ wildly
/// between machines, so being able to ask one directly what it supports is worth the few
/// hundred lines it costs.
/// </summary>
public static class SelfTest
{
    public static string Run()
    {
        var report = new StringBuilder();
        report.AppendLine($"LoopIt7 self test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Windows build {Environment.OSVersion.Version}");
        report.AppendLine($"Process loopback supported: {ProcessLoopbackCapture.IsSupported}");
        report.AppendLine();

        report.AppendLine(CheckVirtualOutput());
        report.AppendLine();

        var sessions = FindPlayingProcesses(report);

        if (!ProcessLoopbackCapture.IsSupported)
        {
            report.AppendLine("Skipping the capture check, this build of Windows is too old.");
            return report.ToString();
        }

        if (sessions.Count == 0)
        {
            report.AppendLine("No application is playing audio right now, so there is nothing to capture.");
            return report.ToString();
        }

        foreach (var (processId, name) in sessions.Take(3))
        {
            report.AppendLine($"--- capturing {name} (pid {processId}) ---");
            report.AppendLine(CaptureOnce(processId));
        }

        return report.ToString();
    }

    /// <summary>
    /// Runs a virtual output on its own for a second and reports what came out of it. No
    /// device is opened and nothing is played: this is the box's own clock and its own mixer,
    /// measured against the wall clock, which is the part that has nothing else to lean on.
    /// </summary>
    private static string CheckVirtualOutput()
    {
        const int seconds = 2;
        const float level = 0.5f;

        var format = WaveFormat.CreateIeeeFloatWaveFormat(BusNode.BusSampleRate, 2);
        var bus = new BusNode("self-test", "Self test");
        var incoming = new Connection("in", "source", "self-test", format);
        var outgoing = new Connection("out", "self-test", "sink", format);

        bus.SetIncoming([incoming]);
        bus.SetConnections([outgoing]);

        // A tenth of a second of steady tone, written in over and over. Constant rather than a
        // sine because what is being measured is the path, not the waveform.
        int framesPerWrite = BusNode.BusSampleRate / 10;
        var block = new byte[framesPerWrite * 8];
        for (int i = 0; i < framesPerWrite * 2; i++)
        {
            BitConverter.GetBytes(level).CopyTo(block, i * 4);
        }

        var tail = outgoing.CreateTail(BusNode.BusSampleRate);
        var scratch = new float[framesPerWrite * 2];

        float peak = 0;
        long framesRead = 0;

        try
        {
            bus.Start(out string? error);
            if (error is not null) return $"virtual output failed to start: {error}";

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                incoming.Write(block, block.Length, 100);

                int read = tail.Read(scratch, 0, scratch.Length);
                framesRead += read / 2;
                for (int i = 0; i < read; i++)
                {
                    float magnitude = Math.Abs(scratch[i]);
                    if (magnitude > peak) peak = magnitude;
                }

                Thread.Sleep(20);
            }

            long rendered = bus.RenderedFrames;
            double elapsed = clock.Elapsed.TotalSeconds;
            double expected = BusNode.BusSampleRate * elapsed;

            // The pump renders in whole blocks, so at any instant it is legitimately up to one
            // block short of the wall clock. Anything beyond that is real drift.
            double behindMs = (expected - rendered) / BusNode.BusSampleRate * 1000.0;
            int blockMs = bus.BlockMilliseconds;

            return $"--- virtual output ---\n" +
                   $"rendered {rendered:N0} frames in {elapsed:0.000}s, expected roughly {expected:N0}\n" +
                   $"{behindMs:0.0} ms behind the wall clock, against a {blockMs} ms render block\n" +
                   $"read back {framesRead:N0} frames, peak {peak:0.0000} against {level:0.0000} in\n" +
                   $"verdict: {Verdict(behindMs, blockMs, peak)}";
        }
        catch (Exception ex)
        {
            return $"virtual output check threw: {ex.Message}";
        }
        finally
        {
            bus.Dispose();
            incoming.Dispose();
            outgoing.Dispose();
        }
    }

    private static string Verdict(double behindMs, int blockMs, float peak) =>
        (behindMs > -2 && behindMs < blockMs + 2, peak > 0.01f) switch
        {
            (true, true) => "the box keeps time and passes signal",
            (true, false) => "clock is right but nothing came through the mixer",
            (false, true) => "signal is passing but the clock is drifting",
            _ => "neither the clock nor the signal is right"
        };

    private static List<(int ProcessId, string Name)> FindPlayingProcesses(StringBuilder report)
    {
        var found = new List<(int, string, float)>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive) continue;

                    int processId = (int)session.GetProcessID;
                    if (processId <= 0) continue;

                    string name = SafeProcessName(processId);
                    float peak = session.AudioMeterInformation.MasterPeakValue;
                    found.Add((processId, name, peak));
                    report.AppendLine($"playing: {name} (pid {processId}) peak {peak:0.0000} on {device.FriendlyName}");
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Session scan failed: {ex.Message}");
        }

        return found
            .GroupBy(f => f.Item1)
            .Select(g => g.OrderByDescending(x => x.Item3).First())
            .OrderByDescending(f => f.Item3)
            .Select(f => (f.Item1, f.Item2))
            .ToList();
    }

    private static string CaptureOnce(int processId)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        using var capture = new ProcessLoopbackCapture(processId, format);

        long bytes = 0;
        float peak = 0;
        Exception? failure = null;
        var finished = new ManualResetEventSlim(false);

        capture.DataAvailable += (_, e) =>
        {
            bytes += e.BytesRecorded;
            for (int i = 0; i + 3 < e.BytesRecorded; i += 4)
            {
                float sample = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                if (sample > peak) peak = sample;
            }
        };

        capture.RecordingStopped += (_, e) =>
        {
            failure = e.Exception;
            finished.Set();
        };

        var clock = Stopwatch.StartNew();
        try
        {
            capture.StartRecording();
            Thread.Sleep(3000);
            capture.StopRecording();
            finished.Wait(2000);
        }
        catch (Exception ex)
        {
            return $"start failed: {ex}";
        }

        if (failure is not null) return $"stopped with an error: {failure}";

        double seconds = clock.Elapsed.TotalSeconds;
        double expected = format.AverageBytesPerSecond * seconds;
        return $"""
            captured {bytes:N0} bytes in {seconds:0.00}s
            expected roughly {expected:N0} bytes at {format.SampleRate} Hz stereo float
            peak {peak:0.0000}
            verdict: {(bytes > 0 ? (peak > 0 ? "audio is flowing" : "stream opened but every sample was silent") : "no data at all")}
            """;
    }

    private static string SafeProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return $"pid {processId}";
        }
    }
}
