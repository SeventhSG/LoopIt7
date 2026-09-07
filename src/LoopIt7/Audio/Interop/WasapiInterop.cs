using System.Runtime.InteropServices;
using NAudio.Wave;

namespace LoopIt7.Audio.Interop;

/// <summary>
/// The slice of WASAPI that NAudio does not surface: activating an audio client against a
/// process rather than an endpoint. This is what lets LoopIt7 take Spotify on its own while
/// leaving the game where it is, with no driver involved.
/// </summary>
internal static class WasapiInterop
{
    /// <summary>The pseudo device path that means "a process, not an endpoint".</summary>
    internal const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    internal const int AudclntShareModeShared = 0;
    internal const int AudclntStreamflagsLoopback = 0x00020000;
    internal const int AudclntStreamflagsEventcallback = 0x00040000;
    internal const int AudclntBufferflagsSilent = 0x2;

    internal static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    internal static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    internal static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);
}

internal enum AudioClientActivationType
{
    Default = 0,
    ProcessLoopback = 1
}

internal enum ProcessLoopbackMode
{
    /// <summary>Capture the target process and everything it spawned.</summary>
    IncludeTargetProcessTree = 0,

    /// <summary>Capture everything else on the machine. Useful for "all but my own game".</summary>
    ExcludeTargetProcessTree = 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    public uint TargetProcessId;
    public ProcessLoopbackMode ProcessLoopbackMode;
}

/// <summary>
/// AUDIOCLIENT_ACTIVATION_PARAMS. The real thing is a tagged union; process loopback is the
/// only member LoopIt7 uses, so it is flattened here.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public AudioClientActivationType ActivationType;
    public AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(
        out int activateResult,
        [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>Marker interface. The activation API refuses handlers that are not agile.</summary>
[ComImport]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
        [In] WaveFormat format, [In] ref Guid audioSessionGuid);

    [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, [In] WaveFormat format, IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead, out uint bufferFlags,
        out long devicePosition, out long qpcPosition);

    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}
