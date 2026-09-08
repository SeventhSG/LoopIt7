using Microsoft.Win32;
using System.IO;
using System.Security;
using System.Security.AccessControl;

namespace LoopIt7.Audio;

/// <summary>The two halves of the name Windows shows for an endpoint.</summary>
/// <param name="Name">The endpoint itself, "Speakers" or "Realtek Digital Output".</param>
/// <param name="InterfaceName">The adapter behind it, shown in brackets after the name.</param>
public sealed record EndpointNames(string Name, string InterfaceName)
{
    /// <summary>How Windows composes the two for a device list. Matches what other apps show.</summary>
    public string Display => string.IsNullOrEmpty(InterfaceName) ? Name : $"{Name} ({InterfaceName})";
}

/// <summary>
/// Renames an audio endpoint, so a cable LoopIt7 installed can carry a LoopIt7 name in every
/// program's device list rather than the cable vendor's.
/// <para>
/// Only ever point this at a cable LoopIt7 installed itself. Devices that were already on the
/// machine are named in other people's OBS scenes, Discord settings and VoiceMeeter configs,
/// and renaming one out from under them breaks setups we did not build.
/// </para>
/// <para>
/// The obvious two routes both fail for a normal user, and it is worth writing down so nobody
/// spends an afternoon rediscovering it. <c>IPropertyStore.SetValue</c> through Core Audio and
/// <c>IPolicyConfig.SetPropertyValue</c> through the audio service both answer E_ACCESSDENIED
/// unelevated. The registry works, but only when the key is opened asking for exactly the
/// rights we need: BUILTIN\Users holds QueryValues and SetValue on the Properties subkey and
/// nothing more, so the usual KEY_WRITE open, which also asks for CreateSubKey, is refused.
/// </para>
/// <para>
/// A written name is live immediately. No audio service restart, no reboot, and the next
/// program to enumerate endpoints sees it.
/// </para>
/// </summary>
public static class EndpointNaming
{
    private const string DeviceRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";

    /// <summary>PKEY_Device_DeviceDesc. The name half, and the one Windows actually reads.</summary>
    private const string NameValue = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

    /// <summary>PKEY_DeviceInterface_FriendlyName. The bracketed half.</summary>
    private const string InterfaceValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    /// <summary>
    /// The rights the ACL grants an ordinary user here, and not one bit more. Asking for
    /// RegistryKeyPermissionCheck alone would open with KEY_WRITE and be refused.
    /// </summary>
    private const RegistryRights WriteRights = RegistryRights.SetValue | RegistryRights.QueryValues;

    /// <summary>
    /// Reads the names an endpoint currently carries, so they can be put back later. Returns
    /// null when the endpoint has no registry entry, which happens for devices that have been
    /// unplugged since Windows last saw them.
    /// </summary>
    public static EndpointNames? TryRead(AudioDeviceInfo device)
    {
        try
        {
            using var key = OpenProperties(device, writable: false);
            if (key is null) return null;

            string name = key.GetValue(NameValue) as string ?? string.Empty;
            string interfaceName = key.GetValue(InterfaceValue) as string ?? string.Empty;

            return name.Length == 0 && interfaceName.Length == 0
                ? null
                : new EndpointNames(name, interfaceName);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gives an endpoint a new name. Returns false with a reason rather than throwing, because
    /// every caller here is doing something optional: a cable that keeps its vendor name still
    /// carries audio perfectly well, and a failure is worth a line in the interface, not a crash.
    /// </summary>
    public static bool TryWrite(AudioDeviceInfo device, EndpointNames names, out string? error)
    {
        try
        {
            using var key = OpenProperties(device, writable: true);
            if (key is null)
            {
                error = "Windows has no record of that endpoint.";
                return false;
            }

            key.SetValue(NameValue, names.Name, RegistryValueKind.String);
            key.SetValue(InterfaceValue, names.InterfaceName, RegistryValueKind.String);

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static RegistryKey? OpenProperties(AudioDeviceInfo device, bool writable)
    {
        string? endpoint = EndpointGuid(device.Id);
        if (endpoint is null) return null;

        // Render and capture ends live under separate roots, and a cable has one of each.
        string flow = device.Kind == AudioSourceKind.Capture ? "Capture" : "Render";
        string path = $@"{DeviceRoot}\{flow}\{endpoint}\Properties";

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        return writable
            ? machine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, WriteRights)
            : machine.OpenSubKey(path);
    }

    /// <summary>
    /// An endpoint id reads "{0.0.0.00000000}.{guid}". The registry is keyed on the trailing
    /// brace pair alone, so take everything from the last opening brace.
    /// </summary>
    private static string? EndpointGuid(string deviceId)
    {
        int start = deviceId.LastIndexOf('{');
        if (start < 0 || !deviceId.EndsWith('}')) return null;

        string candidate = deviceId[start..];
        return Guid.TryParse(candidate, out _) ? candidate : null;
    }
}
