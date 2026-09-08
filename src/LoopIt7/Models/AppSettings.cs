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

public sealed class AppSettings
{
    /// <summary>WASAPI buffer size per endpoint. 3, 5, 10, 20 or 40.</summary>
    public int LatencyMs { get; set; } = 10;

    public List<NodeSettings> Nodes { get; set; } = [];
    public List<CableSettings> Cables { get; set; } = [];
    public List<MidiRouteSettings> MidiRoutes { get; set; } = [];
    public List<PatchPreset> Presets { get; set; } = [];

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
