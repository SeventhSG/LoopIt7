namespace LoopIt7.Audio;

/// <summary>
/// Recognises the virtual audio devices already installed on the machine.
/// <para>
/// Windows will not let a normal program create an audio endpoint: an endpoint comes from a
/// kernel mode driver, and a driver has to be signed by Microsoft before Windows will load
/// it. So LoopIt7 does not pretend to make one. It finds the cables that are already there,
/// labels them clearly, and points at a known good one when none are present.
/// </para>
/// </summary>
public static class VirtualCableService
{
    /// <summary>Where to get a cable when the machine has none. VB-Audio's is free.</summary>
    public const string RecommendedCableUrl = "https://vb-audio.com/Cable/";

    public const string RecommendedCableName = "VB-Audio Virtual Cable";

    private static readonly (string Fragment, string Family)[] KnownCables =
    [
        ("cable input", "VB-Audio Cable"),
        ("cable output", "VB-Audio Cable"),
        ("vb-audio", "VB-Audio Cable"),
        ("voicemeeter", "VoiceMeeter"),
        ("virtual audio cable", "Virtual Audio Cable"),
        ("elgato virtual audio", "Elgato"),
        ("nvidia virtual audio", "NVIDIA Broadcast"),
        ("steam streaming", "Steam"),
        ("virtual cable", "Virtual cable"),
        ("loopit7 cable", "LoopIt7 Cable")
    ];

    /// <summary>
    /// True when this endpoint is a software cable rather than a physical output. Cables are
    /// worth marking in the interface: sending audio to one means handing it to another
    /// program, not to a speaker.
    /// </summary>
    public static bool IsVirtual(AudioDeviceInfo device) => FamilyOf(device) is not null;

    /// <summary>The product a cable belongs to, or null when the endpoint is real hardware.</summary>
    public static string? FamilyOf(AudioDeviceInfo device)
    {
        string haystack = $"{device.Name} {device.InterfaceName}".ToLowerInvariant();

        foreach (var (fragment, family) in KnownCables)
        {
            if (haystack.Contains(fragment, StringComparison.Ordinal)) return family;
        }

        return null;
    }

    public static bool AnyInstalled(IEnumerable<AudioDeviceInfo> devices) => devices.Any(IsVirtual);
}
