namespace LoopIt7.Models;

/// <summary>
/// One box on the patchbay, as it is written to disk. Position is part of the setup: where
/// somebody arranged their boxes is how they read their own routing.
/// </summary>
public sealed class NodeSettings
{
    public string Id { get; set; } = string.Empty;

    /// <summary>"Device", "DeviceLoopback", "Application", "VirtualOutput" or "Destination".</summary>
    public string Kind { get; set; } = "Device";

    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Endpoint id for device nodes. Empty for application nodes.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Executable name for application nodes, used to find the program again.</summary>
    public string ExecutableName { get; set; } = string.Empty;

    /// <summary>
    /// For a virtual output that has been given a way in from Windows: the cable's playback
    /// end, the one other programs choose. Stored because a driver can offer several ends and
    /// working it out again on load would be a fresh guess, not the user's choice.
    /// </summary>
    public string InletFeedDeviceId { get; set; } = string.Empty;

    public double X { get; set; }
    public double Y { get; set; }
    public double GainDb { get; set; }
    public bool Muted { get; set; }

    /// <summary>Stereo balance, -1 hard left to +1 hard right.</summary>
    public double Pan { get; set; }

    public bool Solo { get; set; }

    /// <summary>
    /// For an application node: mute the program in the Windows volume mixer while routing
    /// runs, so it is heard only where LoopIt7 sends it.
    /// </summary>
    public bool Exclusive { get; set; }
}

public sealed class CableSettings
{
    public string Id { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string DestinationId { get; set; } = string.Empty;
    public double GainDb { get; set; }
    public bool Muted { get; set; }
    public int DelayMs { get; set; }
}

public sealed class MidiRouteSettings
{
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
}

/// <summary>A named snapshot of the whole patchbay.</summary>
public sealed class PatchPreset
{
    public string Name { get; set; } = "Preset";
    public int LatencyMs { get; set; } = 10;
    public List<NodeSettings> Nodes { get; set; } = [];
    public List<CableSettings> Cables { get; set; } = [];
}

/// <summary>
/// One tab of the patchbay: its own boxes, its own cables, its own layout. Latency is not
/// part of this, because the WASAPI buffer is a machine setting, not a per patch one.
/// </summary>
public sealed class PatchPageSettings
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = "Page 1";
    public List<NodeSettings> Nodes { get; set; } = [];
    public List<CableSettings> Cables { get; set; } = [];
}

/// <summary>
/// A cable LoopIt7 installed and renamed, and the names it had before, so removing the claim
/// puts the machine back exactly as it was found. Cables that were already on the machine are
/// never recorded here, because they are never renamed.
/// </summary>
public sealed class CableClaimSettings
{
    /// <summary>The playback end, the one other programs pick as a speaker.</summary>
    public string RenderEndpointId { get; set; } = string.Empty;

    /// <summary>The recording end, the one other programs pick as a microphone.</summary>
    public string CaptureEndpointId { get; set; } = string.Empty;

    /// <summary>The name LoopIt7 gave it, without the bracketed half.</summary>
    public string ClaimedName { get; set; } = string.Empty;

    public string OriginalRenderName { get; set; } = string.Empty;
    public string OriginalRenderInterface { get; set; } = string.Empty;
    public string OriginalCaptureName { get; set; } = string.Empty;
    public string OriginalCaptureInterface { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    /// <summary>WASAPI buffer size per endpoint. 3, 5, 10, 20 or 40.</summary>
    public int LatencyMs { get; set; } = 10;

    /// <summary>
    /// Pre pages, the one canvas this app had. Kept only so a settings file saved before pages
    /// existed still has something to migrate into "Page 1"; nothing writes here afterwards.
    /// </summary>
    public List<NodeSettings> Nodes { get; set; } = [];
    public List<CableSettings> Cables { get; set; } = [];
    public List<MidiRouteSettings> MidiRoutes { get; set; } = [];
    public List<PatchPreset> Presets { get; set; } = [];

    /// <summary>The patchbay's tabs, Excel sheet style. Always at least one.</summary>
    public List<PatchPageSettings> Pages { get; set; } = [];

    /// <summary>Which page was open last.</summary>
    public string ActivePageId { get; set; } = string.Empty;

    /// <summary>Cables LoopIt7 installed, renamed, and must put back if it is removed.</summary>
    public List<CableClaimSettings> ClaimedCables { get; set; } = [];

    /// <summary>
    /// Whether the baseline below has been taken. Distinguishes "no cables were here" from
    /// "we have not looked yet", which are opposite answers to the question of what we may
    /// rename.
    /// </summary>
    public bool CableBaselineTaken { get; set; }

    /// <summary>
    /// Every cable endpoint that was on this machine before LoopIt7 ever asked for one. These
    /// belong to somebody else's setup and are never renamed, whatever else happens.
    /// </summary>
    public List<string> ForeignCableIds { get; set; } = [];

    /// <summary>
    /// Cable endpoints that appeared after the user asked LoopIt7 for a cable. Nothing else
    /// on the machine points at these by name yet, so LoopIt7 may name them after the box
    /// they serve.
    /// </summary>
    public List<string> OwnCableIds { get; set; } = [];

    /// <summary>
    /// Cables the user has told LoopIt7 to stop naming. They stay owned, so a claim can be
    /// taken again later, but nothing names them without being asked twice.
    /// </summary>
    public List<string> ReleasedCableIds { get; set; } = [];

    /// <summary>
    /// Set while the user has been sent to get a cable and has not come back with one. It is
    /// the causal link that makes a new cable ours rather than a coincidence: without it, a
    /// VoiceMeeter installed next month would look like something LoopIt7 put there.
    /// </summary>
    public bool AwaitingCable { get; set; }

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoStartRouting { get; set; }
    public bool CloseToTray { get; set; } = true;

    public bool MuteHotkeyEnabled { get; set; } = true;

    /// <summary>Virtual key code for the mute hotkey. Defaults to M.</summary>
    public int MuteHotkeyKey { get; set; } = 0x4D;

    /// <summary>MOD_ALT | MOD_CONTROL.</summary>
    public int MuteHotkeyModifiers { get; set; } = 0x0001 | 0x0002;

    /// <summary>Which workspace was open last. Coming back where you left off costs nothing.</summary>
    public string LastTab { get; set; } = "Patchbay";

    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
}
