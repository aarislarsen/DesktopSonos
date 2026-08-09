using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;

namespace DesktopSonos.Audio;

/// <summary>
/// Fans encoded MP3 frames out to every connected HTTP listener. Slow clients get
/// their oldest buffered chunks dropped rather than stalling the encoder.
/// </summary>
public sealed class AudioBroadcaster
{
    /// <summary>
    /// About two seconds at 192 kbps. Sonos validates a radio URI by connecting and waiting for
    /// audio before it answers the SetAVTransportURI call, and a real-time encoder cannot fill
    /// its buffer fast enough — so a new listener gets this backlog in one burst first.
    /// Larger values connect more reliably but add exactly that much playback latency.
    /// </summary>
    // Every byte of preroll is audio the player hears *late*, so this is a balance: enough for
    // Sonos to fill its decoder and start, not so much that the speaker runs a long way behind
    // the desktop. 24 KB is about one second at 192 kbps.
    private const int MaxPrerollBytes = 24 * 1024;

    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _subscribers = new();
    private readonly Queue<byte[]> _preroll = new();
    private readonly object _prerollLock = new();
    private int _prerollBytes;

    public int SubscriberCount => _subscribers.Count;

    /// <summary>How much encoded audio is ready to burst at the next listener.</summary>
    public int BufferedBytes
    {
        get { lock (_prerollLock) return _prerollBytes; }
    }

    /// <summary>How many bytes the last burst skipped to reach a frame header. Diagnostic.</summary>
    public int LastBurstOffset { get; private set; }

    public event Action<int>? SubscriberCountChanged;

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var id = Guid.NewGuid();

        // Registering inside the same lock that Publish uses means no chunk can slip through
        // the gap between copying the backlog and going live.
        lock (_prerollLock)
        {
            var burst = BuildAlignedBurst();
            if (burst.Length > 0) channel.Writer.TryWrite(burst);
            _subscribers[id] = channel;
        }

        SubscriberCountChanged?.Invoke(_subscribers.Count);
        return new Subscription(this, id, channel.Reader);
    }

    /// <summary>
    /// Flattens the backlog and drops everything before the first MP3 frame header. Once the
    /// ring has wrapped, its first byte lands in the middle of a frame — and a decoder handed a
    /// partial frame at offset zero can sit resyncing instead of ever starting playback.
    /// </summary>
    private byte[] BuildAlignedBurst()
    {
        if (_prerollBytes <= 0) return Array.Empty<byte>();

        var flat = new byte[_prerollBytes];
        var position = 0;
        foreach (var chunk in _preroll)
        {
            Buffer.BlockCopy(chunk, 0, flat, position, chunk.Length);
            position += chunk.Length;
        }

        var start = FindFrameStart(flat);
        LastBurstOffset = start;

        if (start <= 0) return flat;
        if (start >= flat.Length) return Array.Empty<byte>();

        var aligned = new byte[flat.Length - start];
        Buffer.BlockCopy(flat, start, aligned, 0, aligned.Length);
        return aligned;
    }

    /// <summary>Index of the first plausible MPEG audio frame header, or 0 if none is found.</summary>
    private static int FindFrameStart(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0xFF) continue;
            if ((data[i + 1] & 0xE0) != 0xE0) continue;   // 11 sync bits

            var layer = (data[i + 1] >> 1) & 0x03;
            if (layer == 0) continue;                     // reserved

            var bitrateIndex = (data[i + 2] >> 4) & 0x0F;
            if (bitrateIndex is 0 or 0x0F) continue;      // free-form or invalid

            var sampleRateIndex = (data[i + 2] >> 2) & 0x03;
            if (sampleRateIndex == 3) continue;           // reserved

            return i;
        }

        return 0;
    }

    internal void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
            SubscriberCountChanged?.Invoke(_subscribers.Count);
        }
    }

    public void Publish(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return;

        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);

        // The backlog is kept even with nobody listening — that is the whole point of it.
        // Fanning out under the same lock keeps every listener's byte order intact.
        lock (_prerollLock)
        {
            _preroll.Enqueue(copy);
            _prerollBytes += copy.Length;

            while (_prerollBytes > MaxPrerollBytes && _preroll.Count > 1)
                _prerollBytes -= _preroll.Dequeue().Length;

            foreach (var channel in _subscribers.Values)
                channel.Writer.TryWrite(copy);
        }
    }

    public void ResetBacklog()
    {
        lock (_prerollLock)
        {
            _preroll.Clear();
            _prerollBytes = 0;
        }
    }

    /// <summary>Closes every listener — used when the capture stops.</summary>
    public void CompleteAll()
    {
        foreach (var id in _subscribers.Keys.ToArray())
            Unsubscribe(id);

        ResetBacklog();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly AudioBroadcaster _owner;
        private readonly Guid _id;
        private bool _disposed;

        internal Subscription(AudioBroadcaster owner, Guid id, ChannelReader<byte[]> reader)
        {
            _owner = owner;
            _id = id;
            Reader = reader;
        }

        public ChannelReader<byte[]> Reader { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Unsubscribe(_id);
        }
    }
}

/// <summary>Stream adapter so the LAME encoder can write straight into the broadcaster.</summary>
internal sealed class BroadcastSinkStream : Stream
{
    private readonly AudioBroadcaster _broadcaster;
    private readonly Stream? _mirror;
    private readonly long _mirrorLimit;

    public BroadcastSinkStream(AudioBroadcaster broadcaster, Stream? mirror = null,
        long mirrorLimit = 5 * 1024 * 1024)
    {
        _broadcaster = broadcaster;
        _mirror = mirror;
        _mirrorLimit = mirrorLimit;
    }

    private long _written;

    public long BytesWritten => _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    // The encoder may probe these; answering instead of throwing keeps it from dying mid-stream.
    public override long Length => _written;
    public override long Position
    {
        get => _written;
        set { }
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => 0;
    public override long Seek(long offset, SeekOrigin origin) => _written;
    public override void SetLength(long value) { }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_mirror != null && _written < _mirrorLimit)
        {
            try
            {
                _mirror.Write(buffer, offset, count);
                _mirror.Flush();
            }
            catch
            {
                // Diagnostics must never break playback.
            }
        }

        _written += count;
        _broadcaster.Publish(buffer, offset, count);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _mirror?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
