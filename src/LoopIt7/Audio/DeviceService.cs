using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LoopIt7.Audio;

/// <summary>Endpoint property keys NAudio does not expose by name.</summary>
internal static class PropertyKeys
{
    /// <summary>
    /// PKEY_AudioEngine_DeviceFormat: the WAVEFORMATEX blob the Sound control panel writes
    /// when you pick a rate and bit depth on the Advanced tab.
    /// </summary>
    public static readonly PropertyKey AudioEngineDeviceFormat = new()
    {
        formatId = new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"),
        propertyId = 0
    };
}

/// <summary>
/// Enumerates audio endpoints and raises one coalesced event whenever Windows changes them
/// (device plugged, unplugged, disabled, default changed).
/// </summary>
public sealed class DeviceService : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly System.Threading.Lock _sync = new();
    private bool _registered;
    private bool _disposed;

    /// <summary>Raised on a thread pool thread. Marshal to the UI yourself.</summary>
    public event EventHandler? DevicesChanged;

    public DeviceService()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
    }

    public IReadOnlyList<AudioDeviceInfo> GetInputSources()
    {
        var list = new List<AudioDeviceInfo>();
        string defaultCaptureId = TryGetDefaultId(DataFlow.Capture);
        string defaultRenderId = TryGetDefaultId(DataFlow.Render);

        foreach (var device in SafeEnumerate(DataFlow.Capture))
        {
            list.Add(Describe(device, AudioSourceKind.Capture, device.ID == defaultCaptureId));
        }

        // Loopback: every playback endpoint can also be a source. This is how you route
        // "whatever the browser is playing" into a headphone mix.
        foreach (var device in SafeEnumerate(DataFlow.Render))
        {
            list.Add(Describe(device, AudioSourceKind.Loopback, device.ID == defaultRenderId));
        }

        return list;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputs()
    {
        string defaultRenderId = TryGetDefaultId(DataFlow.Render);
        return SafeEnumerate(DataFlow.Render)
            .Select(d => Describe(d, AudioSourceKind.Render, d.ID == defaultRenderId))
            .ToList();
    }

    /// <summary>
    /// Resolves an endpoint id to a live device. Returns null when the device is gone
    /// or currently disabled, which is a normal condition, not an error.
    /// </summary>
    public MMDevice? TryGetDevice(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var device = _enumerator.GetDevice(id);
            return device.State == DeviceState.Active ? device : null;
        }
        catch
        {
            return null;
        }
    }

    public MMDevice? TryGetDefaultDevice(DataFlow flow)
    {
        try
        {
            return _enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)
                ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<MMDevice> SafeEnumerate(DataFlow flow)
    {
        try
        {
            return _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList();
        }
        catch
        {
            return [];
        }
    }

    private string TryGetDefaultId(DataFlow flow)
    {
        using var device = TryGetDefaultDevice(flow);
        return device?.ID ?? string.Empty;
    }

    private static AudioDeviceInfo Describe(MMDevice device, AudioSourceKind kind, bool isDefault)
    {
        string name = device.FriendlyName;
        string iface = device.DeviceFriendlyName;

        // FriendlyName is "Speakers (Realtek Audio)". Strip the interface half so the
        // list reads as one clean name per row with the interface as a subtitle.
        int paren = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(')'))
        {
            name = name[..paren];
        }

        int rate = 0;
        int bits = 0;
        int channels = 0;

        try
        {
            var mix = device.AudioClient.MixFormat;
            rate = mix.SampleRate;
            channels = mix.Channels;

            // The shared engine always runs float internally. What the user set in the Sound
            // panel is the endpoint format, which is the number worth showing.
            bits = device.Properties.Contains(PropertyKeys.AudioEngineDeviceFormat)
                ? ReadEndpointBitDepth(device)
                : mix.BitsPerSample;
        }
        catch
        {
            // Endpoints that are present but not ready report nothing. That is fine.
        }

        return new AudioDeviceInfo(device.ID, name, iface, kind, isDefault, rate, bits, channels);
    }

    /// <summary>
    /// Reads PKEY_AudioEngine_DeviceFormat, the WAVEFORMATEX the Sound control panel writes.
    /// Returns 0 when the blob is missing or shaped unexpectedly.
    /// </summary>
    private static int ReadEndpointBitDepth(MMDevice device)
    {
        try
        {
            object value = device.Properties[PropertyKeys.AudioEngineDeviceFormat].Value;
            if (value is byte[] blob && blob.Length >= 16)
            {
                // WAVEFORMATEX: wFormatTag, nChannels, nSamplesPerSec, nAvgBytesPerSec,
                // nBlockAlign, wBitsPerSample. Bits sit at offset 14.
                return BitConverter.ToInt16(blob, 14);
            }
        }
        catch
        {
        }

        return 0;
    }

    private void Raise() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Raise();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Raise();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Raise();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_registered)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
                _registered = false;
            }
            _enumerator.Dispose();
        }
    }
}
