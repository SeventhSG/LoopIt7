using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace LoopIt7.Audio.Graph;

/// <summary>
/// What WASAPI threw, in words a card can carry. <see cref="Detail"/> is the short line on the
/// card and <see cref="Hint"/>, when there is one, is the tooltip saying what to do about it.
/// </summary>
internal readonly record struct DeviceProblem(string Detail, string? Hint, bool Waiting)
{
    // Audioclient.h
    private const uint DeviceInvalidated = 0x88890004;
    private const uint UnsupportedFormat = 0x88890008;
    private const uint DeviceInUse = 0x8889000A;

    /// <param name="recording">True for a recording endpoint, false for playback or loopback.</param>
    public static DeviceProblem From(Exception ex, MMDevice? device, bool recording)
    {
        if (ex is not COMException com) return new DeviceProblem(ex.Message, null, false);

        return (uint)com.HResult switch
        {
            DeviceInUse => Held(device, recording),
            DeviceInvalidated => new DeviceProblem("Device changed", null, false),
            UnsupportedFormat => new DeviceProblem("Format not accepted", null, false),
            _ => new DeviceProblem("Windows refused the device", $"Windows audio error 0x{com.HResult:X8}.", false)
        };
    }

    /// <summary>
    /// Another program has the device in exclusive mode. Windows gives such a stream the
    /// hardware to itself, so there is nothing to share until it lets go. The node waits
    /// rather than fails, because the retry timer picks it up the moment it is free.
    /// </summary>
    private static DeviceProblem Held(MMDevice? device, bool recording)
    {
        var holder = device is null ? null : ApplicationService.FindHolder(device);
        const string Returns = "LoopIt7 picks the device up on its own as soon as it is free.";

        if (holder is not null && holder.ExecutableName.StartsWith("voicemeeter", StringComparison.OrdinalIgnoreCase))
        {
            string bus = recording ? ", or send it to a B bus there and take that bus here" : string.Empty;
            return new DeviceProblem(
                $"Held by {holder.DisplayName}",
                "VoiceMeeter opens WDM and KS devices for itself alone, and Windows lets nothing else in " +
                $"while it does. In VoiceMeeter, pick the MME entry for this device instead and both can use it{bus}. " +
                Returns,
                true);
        }

        string who = holder?.DisplayName ?? "Another program";
        string where = holder?.DisplayName ?? "that program";
        return new DeviceProblem(
            holder is null ? "Held by another program" : $"Held by {holder.DisplayName}",
            $"{who} opened this device for itself alone, and Windows lets nothing else in while it does. " +
            $"Turn off exclusive mode for this device in {where} and both can use it. " +
            Returns,
            true);
    }
}
