using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace LoopIt7.Audio;

/// <summary>One running program that Windows knows is playing audio.</summary>
public sealed record AudioApplication(int ProcessId, string ExecutableName, string DisplayName, bool IsPlaying)
{
    public string Subtitle => IsPlaying ? "playing" : "idle";
}

/// <summary>
/// Finds the programs worth offering as sources. Windows tracks an audio session per program
/// per endpoint, which is a better list than "every process on the machine": it is short, it
/// is what the user recognises, and everything on it can actually be captured.
/// </summary>
public static class ApplicationService
{
    /// <summary>Sessions belonging to Windows itself, which nobody wants to patch.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "audiodg", "svchost", "LoopIt7"
    };

    public static IReadOnlyList<AudioApplication> GetApplications()
    {
        var found = new Dictionary<int, AudioApplication>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                CollectFrom(device, found);
                device.Dispose();
            }
        }
        catch
        {
            // A machine mid device change can throw here. An empty list is the right answer.
        }

        return found.Values
            .OrderByDescending(a => a.IsPlaying)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void CollectFrom(MMDevice device, Dictionary<int, AudioApplication> found)
    {
        SessionCollection sessions;
        try
        {
            sessions = device.AudioSessionManager.Sessions;
        }
        catch
        {
            return;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            try
            {
                var session = sessions[i];
                int processId = (int)session.GetProcessID;
                if (processId <= 0) continue;

                bool playing = session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive;

                if (found.TryGetValue(processId, out var existing))
                {
                    // The same program can hold a session on several endpoints at once.
                    if (playing && !existing.IsPlaying) found[processId] = existing with { IsPlaying = true };
                    continue;
                }

                string executable = SafeProcessName(processId);
                if (executable.Length == 0 || Ignored.Contains(executable)) continue;

                found[processId] = new AudioApplication(processId, executable, Prettify(executable, session), playing);
            }
            catch
            {
                // Sessions disappear while being enumerated. Skip and carry on.
            }
        }
    }

    /// <summary>
    /// Prefers the window title the program advertises, because "Spotify Premium" beats
    /// "Spotify.exe" in a picker. Falls back to a tidied executable name.
    /// </summary>
    private static string Prettify(string executable, AudioSessionControl session)
    {
        try
        {
            string display = session.DisplayName;
            if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith('@')) return display;
        }
        catch
        {
            // DisplayName is optional and some programs throw rather than return empty.
        }

        return executable.Length > 1
            ? char.ToUpperInvariant(executable[0]) + executable[1..]
            : executable;
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
            return string.Empty;
        }
    }
}
