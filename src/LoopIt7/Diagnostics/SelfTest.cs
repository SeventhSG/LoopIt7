using System.Diagnostics;
using System.Text;
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
