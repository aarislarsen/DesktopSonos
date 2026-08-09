using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DesktopSonos.Audio;

/// <summary>Captures everything mixed to a render endpoint — the "entire desktop" option.</summary>
public sealed class DeviceLoopbackSource : IAudioCaptureSource
{
    private readonly string? _deviceId;
    private readonly string? _label;
    private WasapiLoopbackCapture? _capture;

    /// <param name="label">
    /// Overrides the description shown to the user, for when the device is only a staging post —
    /// an application routed to a spare output is better described by the application's name.
    /// </param>
    public DeviceLoopbackSource(string? deviceId, string? label = null)
    {
        _deviceId = deviceId;
        _label = label;
    }

    public WaveFormat WaveFormat { get; private set; } = new(44100, 16, 2);
    public string Description { get; private set; } = "desktop audio";

    public event Action<byte[], int>? DataAvailable;

    public void Open()
    {
        MMDevice device;
        using (var enumerator = new MMDeviceEnumerator())
        {
            device = string.IsNullOrEmpty(_deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(_deviceId);
        }

        _capture = new WasapiLoopbackCapture(device);

        // The mixer format is normally 32-bit float and often WaveFormatExtensible; reduce it
        // to a plain WaveFormat so the sample-provider chain understands it.
        var format = _capture.WaveFormat;
        if (format is WaveFormatExtensible extensible)
        {
            try { format = extensible.ToStandardWaveFormat(); }
            catch { /* keep the original */ }
        }

        WaveFormat = format;
        Description = _label ?? $"all sound from \"{device.FriendlyName}\"";
        _capture.DataAvailable += OnDataAvailable;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded > 0) DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
    }

    public void Start() => _capture?.StartRecording();

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
    }

    public void Dispose()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
    }
}
