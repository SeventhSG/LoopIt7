namespace LoopIt7.Audio;

/// <summary>
/// A snapshot of one audio endpoint. Deliberately a plain record: live MMDevice objects are
/// COM and must not be held across the device change notifications Windows sends constantly.
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    string InterfaceName,
    AudioSourceKind Kind,
    bool IsSystemDefault,
    int SampleRate = 0,
    int BitDepth = 0,
    int Channels = 0)
{
    /// <summary>Label shown in pickers. Loopback sources say so, plainly.</summary>
    public string DisplayName => Kind switch
    {
        AudioSourceKind.Loopback => $"{Name} (loopback)",
        _ => Name
    };

    /// <summary>Short qualifier next to the name. Kept to a few words so one row fits one line.</summary>
    public string Subtitle => Kind switch
    {
        AudioSourceKind.Capture => IsSystemDefault ? "default input" : InterfaceName,
        AudioSourceKind.Loopback => "system audio",
        _ => IsSystemDefault ? "default output" : InterfaceName
    };

    /// <summary>
    /// The grey line under a device in the Add source and Add output menus: what it does from
    /// LoopIt7's side. Mixer software names its devices from its own side, so VoiceMeeter's
    /// "Voicemeeter Input" is a playback device and its "Voicemeeter Out B1" a recording one,
    /// and without this they read as sitting in the wrong menu.
    /// </summary>
    public string MenuSubtitle => (Kind, VirtualCableService.FamilyOf(this)) switch
    {
        (AudioSourceKind.Loopback, _) => "what this device is playing",
        (AudioSourceKind.Capture, { } family) => $"records what {family} sends out",
        (AudioSourceKind.Render, { } family) => IsSystemDefault
            ? $"plays into {family}, default output"
            : $"plays into {family}",
        _ => Subtitle
    };

    /// <summary>"48 kHz · 24 bit · stereo", or empty when Windows would not say.</summary>
    public string FormatText
    {
        get
        {
            if (SampleRate <= 0) return string.Empty;

            string channels = Channels switch
            {
                1 => "mono",
                2 => "stereo",
                _ => $"{Channels} ch"
            };

            return BitDepth > 0
                ? $"{SampleRate / 1000.0:0.#} kHz · {BitDepth} bit · {channels}"
                : $"{SampleRate / 1000.0:0.#} kHz · {channels}";
        }
    }

    public bool IsInput => Kind == AudioSourceKind.Capture;

    public override string ToString() => DisplayName;
}

public enum AudioSourceKind
{
    /// <summary>A real recording endpoint: microphone, line in, interface input.</summary>
    Capture,

    /// <summary>A playback endpoint tapped in loopback, so we hear what it plays.</summary>
    Loopback,

    /// <summary>A playback endpoint used as a destination.</summary>
    Render
}
