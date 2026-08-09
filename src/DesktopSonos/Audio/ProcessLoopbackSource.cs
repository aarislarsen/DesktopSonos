using System.Runtime.InteropServices;
using NAudio.Wave;

namespace DesktopSonos.Audio;

/// <summary>
/// Captures the audio rendered by one process and its children, using the Windows
/// Application Loopback API (ActivateAudioInterfaceAsync against the "VAD\Process_Loopback"
/// virtual device).
///
/// Two things worth knowing:
///  - This needs Windows build 20348 or newer (Windows 11 in practice). On older builds the
///    activation call fails and the caller should fall back to whole-endpoint loopback.
///  - Browsers do not render audio in the process that owns the window — Chrome and Edge use a
///    separate audio-service child process. That is why INCLUDE_TARGET_PROCESS_TREE is used:
///    targeting the window's process picks up its children too. The flip side is that you get
///    every tab of that browser, not one tab.
///
/// The virtual device has no mix format to query, so the capture format is chosen here. 16-bit
/// 44.1 kHz stereo happens to be exactly what the MP3 encoder wants, so nothing is resampled.
/// </summary>
public sealed class ProcessLoopbackSource : IAudioCaptureSource
{
    private const string VirtualDevicePath = @"VAD\Process_Loopback";

    private const int AudioClientShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int StreamFlagsEventCallback = 0x00040000;
    private const int BufferFlagsSilent = 0x2;
    private const long BufferDurationHns = 2_000_000; // 200 ms

    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly uint _processId;

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private EventWaitHandle? _bufferReady;
    private Thread? _captureThread;
    private volatile bool _running;

    public ProcessLoopbackSource(uint processId, string description)
    {
        _processId = processId;
        Description = description;
    }

    /// <summary>Fixed: the virtual loopback device does not expose a mix format to negotiate.</summary>
    public WaveFormat WaveFormat { get; } = new(44100, 16, 2);

    public string Description { get; }

    public event Action<byte[], int>? DataAvailable;

    /// <summary>True when this Windows build exposes the process-loopback virtual device.</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 20348;

    public void Open()
    {
        if (!IsSupported)
            throw new NotSupportedException(
                "Per-application audio capture needs Windows build 20348 or newer " +
                $"(this machine reports build {Environment.OSVersion.Version.Build}).");

        // ActivateAudioInterfaceAsync completes on an MTA thread pool thread. Calling it from
        // the WPF STA thread and then blocking would deadlock, so the whole activation runs on
        // a dedicated MTA thread.
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { Activate(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "Process loopback activation"
        };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();

        if (failure != null) throw failure;
    }

    private void Activate()
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1,        // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = _processId,
            ProcessLoopbackMode = 0    // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };

        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());

        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propVariant = new PropVariantBlob
            {
                Vt = 0x0041,           // VT_BLOB
                CbSize = (uint)paramsSize,
                PBlobData = paramsPtr
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var handler = new ActivationHandler();
            var iid = IidAudioClient;

            ActivateAudioInterfaceAsync(VirtualDevicePath, ref iid, propVariantPtr, handler, out var operation);

            if (!handler.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The audio system did not answer the process-loopback activation request.");

            operation.GetActivateResult(out var activateResult, out var activatedInterface);
            GC.KeepAlive(handler);

            if (activateResult != 0)
                Marshal.ThrowExceptionForHR(activateResult);

            _audioClient = (IAudioClient)activatedInterface;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }

        var format = new WaveFormatEx
        {
            FormatTag = 1,             // WAVE_FORMAT_PCM
            Channels = 2,
            SamplesPerSec = 44100,
            BitsPerSample = 16,
            BlockAlign = 4,
            AvgBytesPerSec = 44100 * 4,
            CbSize = 0
        };

        var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, formatPtr, false);

            var hr = _audioClient!.Initialize(
                AudioClientShareModeShared,
                StreamFlagsLoopback | StreamFlagsEventCallback,
                BufferDurationHns,
                0,
                formatPtr,
                IntPtr.Zero);

            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }

        _bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        var setHandleResult = _audioClient.SetEventHandle(_bufferReady.SafeWaitHandle.DangerousGetHandle());
        if (setHandleResult != 0) Marshal.ThrowExceptionForHR(setHandleResult);

        var captureIid = IidAudioCaptureClient;
        var serviceResult = _audioClient.GetService(ref captureIid, out var service);
        if (serviceResult != 0) Marshal.ThrowExceptionForHR(serviceResult);

        _captureClient = (IAudioCaptureClient)service;
    }

    public void Start()
    {
        if (_audioClient is null) throw new InvalidOperationException("Open() must be called first.");

        var hr = _audioClient.Start();
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);

        _running = true;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "Process loopback capture",
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        var buffer = new byte[16384];

        try
        {
            while (_running)
            {
                // A process that is silent simply produces no packets; the encoder pads with
                // silence, so a timeout here is normal rather than an error.
                _bufferReady!.WaitOne(200);

                while (_running)
                {
                    if (_captureClient!.GetNextPacketSize(out var packetFrames) != 0 || packetFrames == 0)
                        break;

                    if (_captureClient.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0)
                        break;

                    var byteCount = (int)frames * 4; // 2 channels * 16-bit
                    if (byteCount > 0)
                    {
                        if (buffer.Length < byteCount) buffer = new byte[byteCount];

                        if ((flags & BufferFlagsSilent) != 0)
                            Array.Clear(buffer, 0, byteCount);
                        else
                            Marshal.Copy(data, buffer, 0, byteCount);

                        DataAvailable?.Invoke(buffer, byteCount);
                    }

                    _captureClient.ReleaseBuffer(frames);
                }
            }
        }
        catch (Exception)
        {
            // The target process exiting tears the capture down; treat it as a normal stop.
            _running = false;
        }
    }

    public void Stop()
    {
        _running = false;

        if (_captureThread != null && _captureThread.IsAlive)
            _captureThread.Join(TimeSpan.FromSeconds(1));
        _captureThread = null;

        try { _audioClient?.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();

        if (_captureClient != null)
        {
            Marshal.ReleaseComObject(_captureClient);
            _captureClient = null;
        }

        if (_audioClient != null)
        {
            Marshal.ReleaseComObject(_audioClient);
            _audioClient = null;
        }

        _bufferReady?.Dispose();
        _bufferReady = null;
    }

    // ---------------------------------------------------------------- interop

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    /// <summary>
    /// A PROPVARIANT holding VT_BLOB. On x64 the union starts at offset 8 and the blob pointer
    /// is 8-byte aligned, hence the explicit padding field.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint CbSize;
        public uint Padding;
        public IntPtr PBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _completed = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            _completed.Set();
            return 0;
        }

        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long hnsBufferDuration,
            long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags,
            out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
