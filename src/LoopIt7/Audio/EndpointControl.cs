using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LoopIt7.Audio;

/// <summary>
/// Changes endpoint settings that Windows normally only exposes through the Sound control
/// panel: which device is default, and what format the shared audio engine runs it at.
/// <para>
/// It goes through IPolicyConfig, the interface the control panel itself uses. Microsoft has
/// never documented it, so every call here is treated as allowed to fail and the interface
/// says so rather than pretending the change went through.
/// </para>
/// </summary>
public static class EndpointControl
{
    /// <summary>Formats offered in the device panel, matching what the Sound control panel lists.</summary>
    public static readonly int[] StandardSampleRates = [44100, 48000, 88200, 96000, 176400, 192000];

    public static readonly int[] StandardBitDepths = [16, 24, 32];

    public static bool TrySetDefault(string deviceId, out string? error)
    {
        try
        {
            var config = CreatePolicyConfig();
            try
            {
                // Console, Multimedia and Communications. The control panel sets all three
                // when you pick "Set as default device", so match that.
                for (int role = 0; role <= 2; role++)
                {
                    Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(config);
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TrySetSharedFormat(string deviceId, int sampleRate, int bitDepth, int channels, out string? error)
    {
        try
        {
            var format = new WaveFormatExtensible(sampleRate, bitDepth, channels);
            var config = CreatePolicyConfig();
            try
            {
                Marshal.ThrowExceptionForHR(config.SetDeviceFormat(deviceId, format, format));
            }
            finally
            {
                Marshal.ReleaseComObject(config);
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryResetFormat(string deviceId, out string? error)
    {
        try
        {
            var config = CreatePolicyConfig();
            try
            {
                Marshal.ThrowExceptionForHR(config.ResetDeviceFormat(deviceId));
            }
            finally
            {
                Marshal.ReleaseComObject(config);
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Asks the hardware, in exclusive mode, which of the standard formats it can actually
    /// run. Shared mode would answer yes to everything because the engine would convert.
    /// </summary>
    public static IReadOnlyList<int> GetSupportedSampleRates(MMDevice device, int bitDepth, int channels)
    {
        var supported = new List<int>();

        foreach (int rate in StandardSampleRates)
        {
            try
            {
                var format = new WaveFormatExtensible(rate, bitDepth, channels);
                if (device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, format))
                {
                    supported.Add(rate);
                }
            }
            catch
            {
                // Endpoints that refuse exclusive mode entirely just report nothing.
            }
        }

        return supported;
    }

    /// <summary>Opens the Windows sound settings page, the place these values officially live.</summary>
    public static void OpenWindowsSoundSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:sound",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "control",
                    Arguments = "mmsys.cpl",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Nothing left to try. The caller shows its own message.
            }
        }
    }

    private static IPolicyConfig CreatePolicyConfig()
    {
        var type = Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))
                   ?? throw new PlatformNotSupportedException("This copy of Windows does not expose the audio policy service.");

        return (IPolicyConfig)(Activator.CreateInstance(type)
                               ?? throw new PlatformNotSupportedException("The audio policy service refused to start."));
    }

    /// <summary>
    /// Undocumented, so only the three methods LoopIt7 uses are typed. The others still have
    /// to be declared to keep the vtable slots lined up, and are never called.
    /// </summary>
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceId, bool defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, [In] WaveFormat endpointFormat, [In] WaveFormat mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, bool defaultPeriod, out long period, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceId, ref long period);
        [PreserveSig] int GetShareMode(string deviceId, out IntPtr shareMode);
        [PreserveSig] int SetShareMode(string deviceId, IntPtr shareMode);
        [PreserveSig] int GetPropertyValue(string deviceId, bool endpoint, ref PropertyKey key, out IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceId, bool endpoint, ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(string deviceId, bool visible);
    }
}
