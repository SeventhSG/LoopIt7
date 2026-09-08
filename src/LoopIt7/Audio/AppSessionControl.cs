using NAudio.CoreAudioApi;

namespace LoopIt7.Audio;

/// <summary>
/// Mutes one program in the Windows volume mixer, and puts it back.
/// <para>
/// Capturing a program does not move it. Windows keeps playing it wherever it was already
/// going, so a program sent into a virtual output would otherwise be heard twice: once on its
/// own output and once through the patchbay. Muting its session is the part that makes
/// "assign this program to that output" true rather than nearly true.
/// </para>
/// <para>
/// Windows remembers a session mute across restarts, so every path that sets one has to have
/// a path that clears it. Nothing here is worth leaving behind on somebody's machine.
/// </para>
/// </summary>
public static class AppSessionControl
{
    /// <summary>
    /// Sets the mute flag on every audio session this process owns, on every playback
    /// endpoint. Returns true when at least one session was found and changed.
    /// </summary>
    public static bool SetMuted(int processId, bool muted)
    {
        if (processId <= 0) return false;

        bool touched = false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                touched |= ApplyTo(device, processId, muted);
                device.Dispose();
            }
        }
        catch
        {
            // A machine mid device change throws here. The next pass will catch up.
        }

        return touched;
    }

    /// <summary>True when this program is muted on at least one playback endpoint.</summary>
    public static bool IsMuted(int processId)
    {
        if (processId <= 0) return false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                bool muted = ReadFrom(device, processId);
                device.Dispose();
                if (muted) return true;
            }
        }
        catch
        {
            // Same story: report the safe answer rather than throwing at the caller.
        }

        return false;
    }

    private static bool ApplyTo(MMDevice device, int processId, bool muted)
    {
        bool touched = false;

        SessionCollection sessions;
        try
        {
            sessions = device.AudioSessionManager.Sessions;
        }
        catch
        {
            return false;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            try
            {
                var session = sessions[i];
                if ((int)session.GetProcessID != processId) continue;

                session.SimpleAudioVolume.Mute = muted;
                touched = true;
            }
            catch
            {
                // Sessions disappear while being walked. Skip and carry on.
            }
        }

        return touched;
    }

    private static bool ReadFrom(MMDevice device, int processId)
    {
        SessionCollection sessions;
        try
        {
            sessions = device.AudioSessionManager.Sessions;
        }
        catch
        {
            return false;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            try
            {
                var session = sessions[i];
                if ((int)session.GetProcessID != processId) continue;
                if (session.SimpleAudioVolume.Mute) return true;
            }
            catch
            {
                // Same again.
            }
        }

        return false;
    }
}
