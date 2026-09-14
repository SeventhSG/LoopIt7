using System.Runtime.InteropServices;

namespace LoopIt7.Audio.Interop;

/// <summary>
/// IAudioClient3, which NAudio does not expose. It is what lets a shared stream run at the
/// device's smallest engine period, 3 ms on most USB headsets and 1 ms on a virtual cable,
/// instead of the 10 ms every shared stream gets by default. Windows 10 and later.
/// </summary>
internal static class LowLatencyInterop
{
    internal const int ClsctxAll = 0x17;
    internal const int StreamflagsEventCallback = 0x00040000;
    internal const int BufferflagsSilent = 0x2;

    /// <summary>Another program already fixed the engine period at a different value.</summary>
    internal const int AudclntEEnginePeriodicityLocked = unchecked((int)0x88890028);

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    internal static readonly Guid IidAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    internal static readonly Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    internal static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    internal static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    internal static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    /// <summary>
    /// Puts the calling thread in the Pro Audio scheduling class, so a busy desktop does not
    /// make it miss a 3 ms deadline. Returns the handle to revert with, or zero.
    /// </summary>
    internal static IntPtr EnterProAudio()
    {
        try
        {
            uint index = 0;
            return AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    internal static void LeaveProAudio(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { AvRevertMmThreadCharacteristics(handle); } catch { }
    }

    /// <summary>The fields of a WAVEFORMATEX(TENSIBLE) that the streams need.</summary>
    internal readonly record struct MixFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign, bool IsFloat)
    {
        public static MixFormat Read(IntPtr format)
        {
            ushort tag = (ushort)Marshal.ReadInt16(format, 0);
            int channels = Marshal.ReadInt16(format, 2);
            int rate = Marshal.ReadInt32(format, 4);
            int blockAlign = Marshal.ReadInt16(format, 12);
            int bits = Marshal.ReadInt16(format, 14);

            bool isFloat = tag == 3;
            if (tag == 0xFFFE)
            {
                // WAVEFORMATEXTENSIBLE: the sub format GUID sits after the 18 byte header,
                // the valid bits word and the channel mask.
                var bytes = new byte[16];
                Marshal.Copy(format + 24, bytes, 0, 16);
                isFloat = new Guid(bytes) == SubtypeIeeeFloat;
            }

            return new MixFormat(rate, channels, bits, blockAlign, isFloat);
        }
    }

    /// <summary>
    /// Opens an endpoint in low latency shared mode at the period closest to the one asked
    /// for that the device allows. Throws when the device or Windows will not, so the caller
    /// can fall back to an ordinary shared stream.
    /// </summary>
    internal static LowLatencyStream Open(string deviceId, int requestedMilliseconds)
    {
        // Created by class id and cast to the interface only. The enumerator is one object per
        // process, NAudio has already wrapped it, and .NET hands back NAudio's wrapper for a
        // `new` of a second ComImport class with the same id, which then fails the cast.
        var enumerator = (IMMDeviceEnumeratorLowLatency)Activator.CreateInstance(
            Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!)!;
        IAudioClient3? client = null;
        IntPtr format = IntPtr.Zero;

        try
        {
            client = Activate(enumerator, deviceId);
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out format));
            var mix = MixFormat.Read(format);

            Marshal.ThrowExceptionForHR(client.GetSharedModeEnginePeriod(
                format, out uint defaultPeriod, out uint fundamental, out uint minimum, out uint maximum));

            uint wanted = (uint)Math.Max(1, mix.SampleRate * Math.Max(1, requestedMilliseconds) / 1000);
            uint step = Math.Max(1, fundamental);
            uint period = Math.Clamp((wanted + step - 1) / step * step, minimum, maximum);

            int hr = client.InitializeSharedAudioStream(StreamflagsEventCallback, period, format, IntPtr.Zero);
            if (hr == AudclntEEnginePeriodicityLocked)
            {
                // Somebody else set the engine period first. Join it rather than give up: it
                // is still whatever they asked for, usually low, and never worse than 10 ms.
                Marshal.ThrowExceptionForHR(client.GetCurrentSharedModeEnginePeriod(out IntPtr current, out uint currentPeriod));
                Marshal.FreeCoTaskMem(current);

                Marshal.ReleaseComObject(client);
                client = Activate(enumerator, deviceId);
                period = currentPeriod;
                hr = client.InitializeSharedAudioStream(StreamflagsEventCallback, period, format, IntPtr.Zero);
            }

            Marshal.ThrowExceptionForHR(hr);
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint bufferFrames));

            var stream = new LowLatencyStream(client, format, mix, (int)period, (int)bufferFrames);
            client = null;
            format = IntPtr.Zero;
            return stream;
        }
        finally
        {
            if (client is not null) Marshal.ReleaseComObject(client);
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Opens an endpoint as an ordinary shared stream, but timer driven: a 40 ms buffer that
    /// the output tops up from the device's own padding every millisecond. Normal mode. Waiting
    /// for the engine's wakeups instead crackled on a Behringer, whose driver hands them out in
    /// lumps, taking 2,600 samples a second fewer than it was sent.
    /// </summary>
    internal static LowLatencyStream OpenSharedTimed(string deviceId)
    {
        var enumerator = (IMMDeviceEnumeratorLowLatency)Activator.CreateInstance(
            Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!)!;
        IAudioClient3? client = null;
        IntPtr format = IntPtr.Zero;

        try
        {
            client = Activate(enumerator, deviceId);
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out format));
            var mix = MixFormat.Read(format);

            Marshal.ThrowExceptionForHR(client.GetDevicePeriod(out long defaultPeriod, out _));
            Marshal.ThrowExceptionForHR(client.Initialize(0, 0, 400_000L, 0, format, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint bufferFrames));

            int periodFrames = (int)(defaultPeriod * mix.SampleRate / 10_000_000);
            var stream = new LowLatencyStream(client, format, mix, periodFrames, (int)bufferFrames) { Timed = true };
            client = null;
            format = IntPtr.Zero;
            return stream;
        }
        finally
        {
            if (client is not null) Marshal.ReleaseComObject(client);
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    internal const int ShareModeExclusive = 1;
    internal const int AudclntEBufferSizeNotAligned = unchecked((int)0x88890019);

    /// <summary>
    /// Opens an endpoint in exclusive mode at its smallest period: LoopIt7 talks to the driver
    /// directly, past the Windows mixer and its 10 ms period. 3 ms on a typical USB headset.
    /// Nothing else can use the device while it is held. Throws when the device refuses, is
    /// held by somebody else, or takes none of the formats tried, so the caller can fall back.
    /// </summary>
    /// <param name="deviceFormat">The format picked in the Sound panel, tried first.</param>
    /// <param name="timed">
    /// Timer driven instead of woken by the device, with a buffer of several periods that is
    /// topped up to whatever the device has actually played. For drivers that do not wake the
    /// client every period: a Behringer woke 190 times a second at a 3 ms period on a real
    /// machine, and handing over one period per wakeup fed it 57% of what it played.
    /// </param>
    internal static LowLatencyStream OpenExclusive(string deviceId, byte[]? deviceFormat, int requestedMilliseconds, bool timed = false)
    {
        var enumerator = (IMMDeviceEnumeratorLowLatency)Activator.CreateInstance(
            Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: true)!)!;
        IAudioClient3? client = null;
        IntPtr format = IntPtr.Zero;

        try
        {
            client = Activate(enumerator, deviceId);
            format = FirstSupportedExclusiveFormat(client, deviceFormat)
                ?? throw new NotSupportedException("The device takes none of the formats LoopIt7 can send in exclusive mode.");
            var mix = MixFormat.Read(format);

            // The buffer setting, not the device minimum. Measured on a real machine, a 1 ms
            // exclusive output on a virtual cable woke 580 times a second instead of 1000, so
            // the device went short 40% of the time and tore the sound; 3 ms held steady.
            Marshal.ThrowExceptionForHR(client.GetDevicePeriod(out _, out long minimum));
            long period = Math.Max(minimum, Math.Max(3, requestedMilliseconds) * 10_000L);

            // Woken: the buffer is exactly one period. Timed: several periods, at least 15 ms, so
            // a driver that plays in 10 ms lumps never finds it empty between two top ups.
            int flags = timed ? 0 : StreamflagsEventCallback;
            long buffer = timed ? Math.Max(period * 4, 150_000L) : period;

            int hr = client.Initialize(ShareModeExclusive, flags, buffer, period, format, IntPtr.Zero);
            if (hr == AudclntEBufferSizeNotAligned)
            {
                // The driver wants the buffer on its own boundary. It says which through the
                // buffer size of the failed attempt, and the client has to be made again.
                Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint aligned));
                buffer = (long)(10_000_000.0 * aligned / mix.SampleRate + 0.5);
                if (!timed) period = buffer;

                Marshal.ReleaseComObject(client);
                client = Activate(enumerator, deviceId);
                hr = client.Initialize(ShareModeExclusive, flags, buffer, period, format, IntPtr.Zero);
            }

            Marshal.ThrowExceptionForHR(hr);
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint bufferFrames));

            int periodFrames = timed ? (int)(period * mix.SampleRate / 10_000_000) : (int)bufferFrames;
            var stream = new LowLatencyStream(client, format, mix, periodFrames, (int)bufferFrames) { Exclusive = true, Timed = timed };
            client = null;
            format = IntPtr.Zero;
            return stream;
        }
        finally
        {
            if (client is not null) Marshal.ReleaseComObject(client);
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// The Sound panel's format first, since that is what the device is set up for, then the
    /// common ones. Returns a CoTaskMem pointer the caller owns, or null.
    /// </summary>
    private static IntPtr? FirstSupportedExclusiveFormat(IAudioClient3 client, byte[]? deviceFormat)
    {
        var candidates = new List<IntPtr>();
        int channels = 2;

        if (deviceFormat is { Length: >= 18 })
        {
            IntPtr blob = Marshal.AllocCoTaskMem(deviceFormat.Length);
            Marshal.Copy(deviceFormat, 0, blob, deviceFormat.Length);
            candidates.Add(blob);
            channels = Math.Max(1, (int)BitConverter.ToInt16(deviceFormat, 2));
        }

        foreach (int rate in new[] { 48000, 44100, 96000 })
        {
            candidates.Add(Extensible(rate, channels, 24, 24, false));
            candidates.Add(Extensible(rate, channels, 32, 24, false));
            candidates.Add(Extensible(rate, channels, 16, 16, false));
            candidates.Add(Extensible(rate, channels, 32, 32, true));
        }

        IntPtr? chosen = null;
        foreach (var candidate in candidates)
        {
            if (chosen is null && client.IsFormatSupported(ShareModeExclusive, candidate, out IntPtr closest) == 0)
            {
                chosen = candidate;
                if (closest != IntPtr.Zero) Marshal.FreeCoTaskMem(closest);
                continue;
            }

            Marshal.FreeCoTaskMem(candidate);
        }

        return chosen;
    }

    /// <summary>A WAVEFORMATEXTENSIBLE in CoTaskMem.</summary>
    private static IntPtr Extensible(int rate, int channels, int bits, int validBits, bool isFloat)
    {
        IntPtr p = Marshal.AllocCoTaskMem(40);
        int blockAlign = channels * bits / 8;
        Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
        Marshal.WriteInt16(p, 2, (short)channels);
        Marshal.WriteInt32(p, 4, rate);
        Marshal.WriteInt32(p, 8, rate * blockAlign);
        Marshal.WriteInt16(p, 12, (short)blockAlign);
        Marshal.WriteInt16(p, 14, (short)bits);
        Marshal.WriteInt16(p, 16, 22);
        Marshal.WriteInt16(p, 18, (short)validBits);
        Marshal.WriteInt32(p, 20, channels == 1 ? 0x4 : 0x3);
        var subtype = isFloat ? SubtypeIeeeFloat : new Guid("00000001-0000-0010-8000-00aa00389b71");
        Marshal.Copy(subtype.ToByteArray(), 0, p + 24, 16);
        return p;
    }

    private static IAudioClient3 Activate(IMMDeviceEnumeratorLowLatency enumerator, string deviceId)
    {
        Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out var device));
        try
        {
            Marshal.ThrowExceptionForHR(device.Activate(IidAudioClient3, ClsctxAll, IntPtr.Zero, out object instance));
            return (IAudioClient3)instance;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }
}

/// <summary>An initialised low latency client, its mix format and its period.</summary>
internal sealed class LowLatencyStream(IAudioClient3 client, IntPtr format, LowLatencyInterop.MixFormat mix, int periodFrames, int bufferFrames) : IDisposable
{
    public IAudioClient3 Client { get; } = client;
    public LowLatencyInterop.MixFormat Mix { get; } = mix;
    public int PeriodFrames { get; } = periodFrames;
    public int BufferFrames { get; } = bufferFrames;

    public double PeriodMilliseconds => PeriodFrames * 1000.0 / Mix.SampleRate;

    /// <summary>True when the device is held exclusively, past the Windows mixer.</summary>
    public bool Exclusive { get; init; }

    /// <summary>True when topped up on a timer instead of woken by the device.</summary>
    public bool Timed { get; init; }

    public double BufferMilliseconds => BufferFrames * 1000.0 / Mix.SampleRate;

    /// <summary>"exclusive · 144 samples (3.0 ms)", for the box. A timed stream reports what it keeps queued.</summary>
    public string Describe(int keptFrames = 0) => Timed
        ? $"{(Exclusive ? "exclusive" : "shared")}, timed · {(keptFrames > 0 ? keptFrames : BufferFrames)} samples ({(keptFrames > 0 ? keptFrames : BufferFrames) * 1000.0 / Mix.SampleRate:0.0} ms)"
        : $"{(Exclusive ? "exclusive" : "shared")} · {PeriodFrames} samples ({PeriodMilliseconds:0.0} ms)";

    private IntPtr _format = format;

    public void Dispose()
    {
        try { Marshal.ReleaseComObject(Client); } catch { }
        if (_format != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_format);
            _format = IntPtr.Zero;
        }
    }
}

/// <summary>Only the members up to GetDevice; the vtable is not read past it.</summary>
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorLowLatency
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceLowLatency device);
}

/// <summary>Only Activate, the first member.</summary>
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceLowLatency
{
    [PreserveSig]
    int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

/// <summary>IAudioClient, IAudioClient2 and IAudioClient3, in vtable order.</summary>
[ComImport]
[Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient3
{
    // IAudioClient
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    // IAudioClient2
    [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
    [PreserveSig] int SetClientProperties(IntPtr properties);
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minBufferDuration, out long maxBufferDuration);

    // IAudioClient3
    [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodInFrames, out uint fundamentalPeriodInFrames, out uint minPeriodInFrames, out uint maxPeriodInFrames);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodInFrames);
    [PreserveSig] int InitializeSharedAudioStream(int streamFlags, uint periodInFrames, IntPtr format, IntPtr audioSessionGuid);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
}
