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

    /// <summary>
    /// A cable has two ends: the playback endpoint you send to, and the recording endpoint the
    /// other program picks up from. Windows never says which belongs to which, so this pairs
    /// them by driver. Getting this wrong is the single most common way a routing setup fails,
    /// and it fails silently, so the answer is worth showing on screen.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> FindPickupEndpoints(
        AudioDeviceInfo destination,
        IEnumerable<AudioDeviceInfo> captureDevices)
    {
        if (!IsVirtual(destination)) return [];

        string family = FamilyOf(destination) ?? string.Empty;

        var candidates = captureDevices
            .Where(d => d.Kind == AudioSourceKind.Capture && IsVirtual(d))
            .Where(d => string.Equals(FamilyOf(d), family, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count <= 1) return candidates;

        // Several ends from the same driver, as Elgato and VoiceMeeter both have. Prefer the
        // one whose interface string matches exactly before falling back to the whole set.
        var sameInterface = candidates
            .Where(d => string.Equals(d.InterfaceName, destination.InterfaceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return sameInterface.Count > 0 ? sameInterface : candidates;
    }

    /// <summary>One sentence telling the user what to select in the other program.</summary>
    public static string? DescribePickup(AudioDeviceInfo destination, IEnumerable<AudioDeviceInfo> captureDevices)
    {
        var pickups = FindPickupEndpoints(destination, captureDevices);
        if (pickups.Count == 0) return null;

        string names = pickups.Count == 1
            ? $"\"{pickups[0].Name}\""
            : string.Join(" or ", pickups.Select(p => $"\"{p.Name}\""));

        return $"Send audio here, then choose {names} as the input in the other program.";
    }
}
