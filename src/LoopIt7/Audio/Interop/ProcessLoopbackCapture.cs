using System.Runtime.InteropServices;
using NAudio.Wave;

namespace LoopIt7.Audio.Interop;

/// <summary>
/// Captures the audio a single process (and its children) is playing, without a virtual
/// device and without a driver. Windows 10 build 20348 and later only.
/// <para>
/// It implements NAudio's <see cref="IWaveIn"/> so the rest of the engine cannot tell the
/// difference between an application, a microphone and a device loopback.
/// </para>
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const int WaitTimeoutMs = 300;

    private readonly uint _processId;
    private readonly bool _excludeProcessTree;
    private readonly int _bufferMilliseconds;

    private Thread? _thread;
    private volatile bool _stopRequested;
    private IntPtr _bufferEvent = IntPtr.Zero;

    public ProcessLoopbackCapture(int processId, WaveFormat format, bool excludeProcessTree = false, int bufferMilliseconds = 20)
    {
        _processId = (uint)processId;
        _excludeProcessTree = excludeProcessTree;
        _bufferMilliseconds = Math.Clamp(bufferMilliseconds, 5, 200);
        WaveFormat = format;
    }

    /// <summary>
    /// The format Windows will convert the process audio into. Process loopback lets the
    /// caller name the format, so the engine asks for its own bus format and skips a resample.
    /// </summary>
    public WaveFormat WaveFormat { get; set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    /// <summary>True when this build of Windows exposes process loopback capture.</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 20348;

    public void StartRecording()
    {
        if (_thread is not null) throw new InvalidOperationException("Already recording.");

        _stopRequested = false;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"LoopIt7 process capture {_processId}",
            Priority = ThreadPriority.AboveNormal
        };

        // The activation callback arrives on a pool thread, so this one must be agile.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void StopRecording()
    {
        _stopRequested = true;
        if (_bufferEvent != IntPtr.Zero) Native.SetEvent(_bufferEvent);

        var thread = _thread;
        _thread = null;
        thread?.Join(2000);
    }

    private void CaptureLoop()
    {
        Exception? failure = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr paramsPtr = IntPtr.Zero;

        try
        {
            var activation = new AudioClientActivationParams
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = _processId,
                    ProcessLoopbackMode = _excludeProcessTree
                        ? ProcessLoopbackMode.ExcludeTargetProcessTree
                        : ProcessLoopbackMode.IncludeTargetProcessTree
                }
            };

            paramsPtr = PropVariant.CreateBlob(activation);
            client = Activate(paramsPtr);

            _bufferEvent = Native.CreateEventW(IntPtr.Zero, false, false, null);
            if (_bufferEvent == IntPtr.Zero) throw new InvalidOperationException("Could not create the capture event.");

            long duration = _bufferMilliseconds * 10_000L;
            var session = Guid.Empty;
            int flags = WasapiInterop.AudclntStreamflagsLoopback | WasapiInterop.AudclntStreamflagsEventcallback;

            int hr = client.Initialize(WasapiInterop.AudclntShareModeShared, flags, duration, 0, WaveFormat, ref session);
            if (hr < 0)
            {
                // Process loopback historically wanted the auto convert flag in the periodicity
                // slot. Some builds still insist on it, so fall back rather than give up.
                const long autoConvertQuirk = unchecked((int)0x80000000);
                hr = client.Initialize(WasapiInterop.AudclntShareModeShared, flags, duration, autoConvertQuirk, WaveFormat, ref session);
            }

            Marshal.ThrowExceptionForHR(hr);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(_bufferEvent));
            Marshal.ThrowExceptionForHR(client.GetService(WasapiInterop.IidAudioCaptureClient, out object service));

            capture = (IAudioCaptureClient)service;
            Marshal.ThrowExceptionForHR(client.Start());

            Pump(capture);

            client.Stop();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
            if (paramsPtr != IntPtr.Zero) PropVariant.FreeBlob(paramsPtr);

            if (_bufferEvent != IntPtr.Zero)
            {
                Native.CloseHandle(_bufferEvent);
                _bufferEvent = IntPtr.Zero;
            }

            RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }

    private IAudioClient Activate(IntPtr activationParams)
    {
        var handler = new ActivationHandler();
        WasapiInterop.ActivateAudioInterfaceAsync(
            WasapiInterop.VirtualAudioDeviceProcessLoopback,
            WasapiInterop.IidAudioClient,
            activationParams,
            handler,
            out var operation);

        if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Windows did not answer the process capture request.");
        }

        Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int activateResult, out object client));
        Marshal.ThrowExceptionForHR(activateResult);

        return (IAudioClient)client;
    }

    private void Pump(IAudioCaptureClient capture)
    {
        byte[] managed = new byte[WaveFormat.AverageBytesPerSecond];

        while (!_stopRequested)
        {
            Native.WaitForSingleObject(_bufferEvent, WaitTimeoutMs);
            if (_stopRequested) break;

            while (true)
            {
                if (capture.GetNextPacketSize(out uint packetFrames) < 0 || packetFrames == 0) break;

                int hr = capture.GetBuffer(out IntPtr data, out uint frames, out uint bufferFlags, out _, out _);
                if (hr < 0 || frames == 0) break;

                int byteCount = (int)frames * WaveFormat.BlockAlign;
                if (managed.Length < byteCount) managed = new byte[byteCount];

                if ((bufferFlags & WasapiInterop.AudclntBufferflagsSilent) != 0 || data == IntPtr.Zero)
                {
                    // A silent packet still has to reach the mixer, otherwise a paused app
                    // leaves its last block ringing in every destination buffer.
                    Array.Clear(managed, 0, byteCount);
                }
                else
                {
                    Marshal.Copy(data, managed, 0, byteCount);
                }

                capture.ReleaseBuffer(frames);
                DataAvailable?.Invoke(this, new WaveInEventArgs(managed, byteCount));
            }
        }
    }

    public void Dispose() => StopRecording();

    /// <summary>Signals the waiting capture thread once Windows has activated the client.</summary>
    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Completed { get; } = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Completed.Set();
            return 0;
        }
    }

    private static class PropVariant
    {
        private const ushort VtBlob = 65;

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public ushort VariantType;
            public ushort Reserved1;
            public ushort Reserved2;
            public ushort Reserved3;
            public int Size;
            public IntPtr Data;
            public IntPtr Padding;
        }

        public static IntPtr CreateBlob<T>(T value) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            IntPtr data = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(value, data, false);

            var blob = new Blob { VariantType = VtBlob, Size = size, Data = data };
            IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<Blob>());
            Marshal.StructureToPtr(blob, variant, false);
            return variant;
        }

        public static void FreeBlob(IntPtr variant)
        {
            var blob = Marshal.PtrToStructure<Blob>(variant);
            if (blob.Data != IntPtr.Zero) Marshal.FreeHGlobal(blob.Data);
            Marshal.FreeHGlobal(variant);
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    }
}
