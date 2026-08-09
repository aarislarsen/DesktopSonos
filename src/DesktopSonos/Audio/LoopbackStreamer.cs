using System.Diagnostics;
using System.IO;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DesktopSonos.Audio;

/// <summary>
/// Encodes whatever an <see cref="IAudioCaptureSource"/> produces into MP3 in real time and
/// publishes the frames to <see cref="Broadcaster"/>.
///
/// Notes:
///  - Loopback capture delivers nothing while the source is silent. A radio stream that stops
///    sending bytes makes Sonos drop the connection, so the pump thread paces itself against the
///    wall clock and BufferedWaveProvider.ReadFully pads the gaps with silence.
///  - The capture device is deliberately kept behind a swappable "stage" so it can be changed
///    without restarting the encoder. Restarting the encoder would end the MP3 stream and drop
///    every connected player; swapping the stage only costs a few milliseconds of silence.
///  - Expect ~1-2 s of latency end to end. That is Sonos' own jitter buffer, not this code.
/// </summary>
public sealed class LoopbackStreamer : IDisposable
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int BitsPerSample = 16;

    private static readonly WaveFormat TargetFormat = new(SampleRate, BitsPerSample, Channels);
    private static readonly int BytesPerSecond = SampleRate * Channels * BitsPerSample / 8;

    private readonly object _sync = new();

    /// <summary>
    /// One capture device plus the conversion chain that turns it into the encoder's format.
    /// Everything that depends on the device's own format lives in here, which is what makes the
    /// device replaceable mid-stream.
    /// </summary>
    private sealed class CaptureStage : IDisposable
    {
        private readonly BufferedWaveProvider _buffer;
        private bool _capturing;

        public CaptureStage(IAudioCaptureSource source, float gainLinear)
        {
            Source = source;

            var captureFormat = source.WaveFormat;
            _buffer = new BufferedWaveProvider(captureFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                DiscardOnBufferOverflow = true,
                ReadFully = true              // pad with silence instead of returning 0 bytes
            };

            // Everything below is managed code on purpose. MediaFoundationResampler is a COM
            // object: constructing it here (the WPF STA thread) and then calling Read from the
            // pump thread (MTA) throws, which silently killed the encoder. The WDL resampler has
            // no such constraint.
            ISampleProvider samples = _buffer.ToSampleProvider();

            if (samples.WaveFormat.Channels == 1)
            {
                samples = new MonoToStereoSampleProvider(samples);
            }
            else if (samples.WaveFormat.Channels > Channels)
            {
                // Surround mixdown is not the job here; take the front pair.
                var downmix = new MultiplexingSampleProvider(new[] { samples }, Channels);
                downmix.ConnectInputToOutput(0, 0);
                downmix.ConnectInputToOutput(1, 1);
                samples = downmix;
            }

            if (samples.WaveFormat.SampleRate != SampleRate)
                samples = new WdlResamplingSampleProvider(samples, SampleRate);

            // Loopback captures the endpoint mix *after* Windows applies master volume, so a
            // quiet desktop yields a quiet stream. Boosting here — still in 32-bit float, before
            // the 16-bit conversion — costs essentially no quality.
            Gain = new VolumeSampleProvider(samples) { Volume = gainLinear };
            Output = Gain.ToWaveProvider16();

            source.DataAvailable += OnSourceData;
        }

        public IAudioCaptureSource Source { get; }
        public VolumeSampleProvider Gain { get; }
        public IWaveProvider Output { get; }

        /// <summary>Raw bytes handed over by this device. Zero means it is delivering nothing.</summary>
        public long CapturedBytes { get; private set; }

        public TimeSpan Backlog => _buffer.BufferedDuration;

        public void StartCapture()
        {
            Source.Start();
            _capturing = true;
        }

        private void OnSourceData(byte[] buffer, int count)
        {
            if (count <= 0) return;
            CapturedBytes += count;
            _buffer.AddSamples(buffer, 0, count);
        }

        public void Dispose()
        {
            Source.DataAvailable -= OnSourceData;
            if (_capturing)
            {
                try { Source.Stop(); } catch { }
                _capturing = false;
            }
            try { Source.Dispose(); } catch { }
        }
    }

    private volatile CaptureStage? _stage;
    private LameMP3FileWriter? _encoder;
    private float _gainLinear = 1f;
    private BroadcastSinkStream? _sink;
    private Thread? _pump;
    private volatile bool _running;
    private volatile bool _sourceChanged;

    public AudioBroadcaster Broadcaster { get; } = new();

    /// <summary>128-320. 192 is a good balance for a LAN.</summary>
    public int BitrateKbps { get; set; } = 192;

    /// <summary>Linear make-up gain applied before encoding. Live-adjustable.</summary>
    public float GainLinear
    {
        get => _gainLinear;
        set
        {
            _gainLinear = Math.Clamp(value, 1f, 64f);
            var stage = _stage;
            if (stage != null) stage.Gain.Volume = _gainLinear;
        }
    }

    public bool IsRunning => _running;

    /// <summary>What is currently being captured, for the UI.</summary>
    public string SourceDescription { get; private set; } = "";

    /// <summary>Total MP3 bytes produced since Start. Zero means the pipeline is not working.</summary>
    public long EncodedBytes => _sink?.BytesWritten ?? 0;

    /// <summary>
    /// A copy of the encoded stream is mirrored here (capped) purely so the output can be
    /// checked in a normal audio player when something does not sound right.
    /// </summary>
    public string? DebugDumpPath { get; private set; }

    /// <summary>Set when the pump thread dies, so the failure is not silent.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raw bytes handed over by the current capture device.</summary>
    public long CapturedBytes => _stage?.CapturedBytes ?? 0;

    /// <summary>Peak amplitude of the most recent chunk, 0..1.</summary>
    public float PeakLevel { get; private set; }

    /// <summary>Peak as dBFS text, or "silent".</summary>
    public string LevelText => PeakLevel <= 0.0001f
        ? "silent"
        : $"{20 * Math.Log10(PeakLevel):0.0} dBFS";

    public event Action<string>? Log;

    /// <summary>Takes ownership of <paramref name="source"/> and disposes it on Stop.</summary>
    public void Start(IAudioCaptureSource source)
    {
        lock (_sync)
        {
            if (_running)
            {
                source.Dispose();
                return;
            }

            var stage = OpenStage(source);

            _stage = stage;
            SourceDescription = source.Description;
            LastError = null;
            PeakLevel = 0;

            Stream? mirror = null;
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopSonos");
                Directory.CreateDirectory(folder);
                DebugDumpPath = Path.Combine(folder, "stream-debug.mp3");
                mirror = new FileStream(DebugDumpPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            catch
            {
                DebugDumpPath = null;
            }

            try
            {
                _sink = new BroadcastSinkStream(Broadcaster, mirror);
                _encoder = new LameMP3FileWriter(_sink, TargetFormat, BitrateKbps);
            }
            catch
            {
                stage.Dispose();
                _stage = null;
                CleanUpEncoder();
                throw;
            }

            _running = true;
            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "DesktopSonos MP3 pump",
                Priority = ThreadPriority.AboveNormal
            };
            _pump.Start();
        }
    }

    /// <summary>
    /// Replaces the capture device while the stream stays up, so players never see the connection
    /// close. If the new device cannot be opened the old one keeps running and this throws.
    /// </summary>
    public void SwitchSource(IAudioCaptureSource source)
    {
        lock (_sync)
        {
            if (!_running)
            {
                source.Dispose();
                throw new InvalidOperationException("Not streaming.");
            }

            // Opened before the old stage is touched: a failure here must not kill the stream.
            var stage = OpenStage(source);

            var previous = _stage;
            _stage = stage;                     // the pump reads this on its next iteration
            SourceDescription = source.Description;
            PeakLevel = 0;
            _sourceChanged = true;

            previous?.Dispose();
            Log?.Invoke($"Capture switched to {source.Description}; the stream was not interrupted.");
        }
    }

    /// <summary>Opens a device and builds its conversion chain. Disposes the source on failure.</summary>
    private CaptureStage OpenStage(IAudioCaptureSource source)
    {
        try
        {
            source.Open();
        }
        catch
        {
            source.Dispose();
            throw;
        }

        var captureFormat = source.WaveFormat;
        Log?.Invoke($"Capturing {source.Description} at {captureFormat.SampleRate} Hz, " +
                    $"{captureFormat.Channels} ch, {captureFormat.BitsPerSample}-bit " +
                    $"{captureFormat.Encoding}");

        CaptureStage stage;
        try
        {
            stage = new CaptureStage(source, _gainLinear);
        }
        catch
        {
            source.Dispose();
            throw;
        }

        try
        {
            stage.StartCapture();
        }
        catch
        {
            stage.Dispose();
            throw;
        }

        return stage;
    }

    /// <summary>
    /// Reads the converted PCM at real-time speed and feeds the encoder. Reading faster than the
    /// clock would make Sonos buffer forever; slower would underrun.
    /// </summary>
    private void PumpLoop()
    {
        // One MPEG-1 Layer III frame is 1152 samples; 4 frames ~= 104 ms.
        const int chunkSize = 1152 * Channels * (BitsPerSample / 8) * 4;
        var buffer = new byte[chunkSize];
        var clock = Stopwatch.StartNew();
        long producedBytes = 0;

        // Level reporting: enough to tell "wrong device" from "device is delivering silence",
        // without filling the log while it is working normally.
        var nextReport = TimeSpan.FromSeconds(2);
        var peakSinceReport = 0;
        bool? wasSilent = null;

        try
        {
            while (_running)
            {
                var stage = _stage;

                if (_sourceChanged)
                {
                    // Say something about the new device rather than staying quiet because the
                    // old one was in the same state.
                    _sourceChanged = false;
                    wasSilent = null;
                    peakSinceReport = 0;
                    nextReport = clock.Elapsed + TimeSpan.FromSeconds(2);
                }

                var scheduled = (long)(clock.Elapsed.TotalSeconds * BytesPerSecond);

                // If the capture device has run ahead of us (clock drift between the audio
                // hardware and the system timer) let the pump catch up instead of discarding.
                var backlog = stage?.Backlog ?? TimeSpan.Zero;
                if (producedBytes >= scheduled && backlog < TimeSpan.FromSeconds(1))
                {
                    Thread.Sleep(5);
                    continue;
                }

                var read = stage?.Output.Read(buffer, 0, chunkSize) ?? 0;
                if (read <= 0)
                {
                    // No device for a moment (a switch is in flight). Silence keeps the MP3
                    // stream alive, which is the whole point of not restarting the encoder.
                    Array.Clear(buffer, 0, chunkSize);
                    read = chunkSize;
                }

                // Deliberately no Flush() here. In NAudio.Lame a flush can finalise the encoder's
                // stream, which would emit a run of separately-terminated MP3 fragments rather
                // than one continuous stream — decoders accept the bytes and then play nothing.
                // Writing ~104 ms per iteration is plenty to keep frames coming out on its own.
                var peak = PeakOf(buffer, read);
                PeakLevel = peak / 32768f;
                if (peak > peakSinceReport) peakSinceReport = peak;

                _encoder!.Write(buffer, 0, read);
                producedBytes += read;

                if (clock.Elapsed >= nextReport)
                {
                    nextReport = clock.Elapsed + TimeSpan.FromSeconds(2);
                    var silent = peakSinceReport == 0;
                    var captured = stage?.CapturedBytes ?? 0;

                    // Only speak up on the first report and whenever it changes state.
                    if (wasSilent != silent)
                    {
                        wasSilent = silent;
                        Log?.Invoke(silent
                            ? $"Capture is SILENT — {captured / 1024} KB arrived from the device. " +
                              (captured == 0
                                  ? "Nothing at all is being rendered to it; pick the output Windows is actually playing to."
                                  : "The device is delivering digital silence.")
                            : $"Capture level {20 * Math.Log10(peakSinceReport / 32768.0):0.0} dBFS " +
                              $"({captured / 1024} KB from the device).");
                    }

                    peakSinceReport = 0;
                }
            }
        }
        catch (Exception ex) when (ex is not ThreadInterruptedException)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            Log?.Invoke($"Encoder stopped after {producedBytes / 1024} KB — {LastError}");
        }
    }

    /// <summary>Largest absolute sample in a 16-bit PCM buffer.</summary>
    private static int PeakOf(byte[] buffer, int count)
    {
        var peak = 0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }
        return peak;
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_running) return;
            _running = false;

            var stage = _stage;
            _stage = null;

            if (_pump != null && _pump.IsAlive && !_pump.Join(TimeSpan.FromSeconds(2)))
                Log?.Invoke("Pump thread did not exit cleanly.");
            _pump = null;

            stage?.Dispose();

            CleanUpEncoder();
            SourceDescription = "";
            Broadcaster.CompleteAll();
        }
    }

    private void CleanUpEncoder()
    {
        // Disposing the writer emits LAME's final frames into the sink; harmless.
        try { _encoder?.Dispose(); } catch { }
        _encoder = null;

        try { _sink?.Dispose(); } catch { }
        _sink = null;
    }

    public void Dispose() => Stop();
}
