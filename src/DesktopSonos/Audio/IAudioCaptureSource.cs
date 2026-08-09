using NAudio.Wave;

namespace DesktopSonos.Audio;

/// <summary>
/// Something that produces raw PCM for the encoder. Two implementations exist: whole-endpoint
/// loopback (everything the PC plays) and per-process loopback (one application).
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    /// <summary>Valid only after <see cref="Open"/>.</summary>
    WaveFormat WaveFormat { get; }

    /// <summary>Human-readable label for logs and the UI.</summary>
    string Description { get; }

    /// <summary>Acquires the device and settles the wave format. Throws if unavailable.</summary>
    void Open();

    void Start();
    void Stop();

    /// <summary>Raised on a capture thread with (buffer, byteCount). The buffer is reused.</summary>
    event Action<byte[], int>? DataAvailable;
}
