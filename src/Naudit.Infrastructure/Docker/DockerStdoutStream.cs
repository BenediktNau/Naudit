namespace Naudit.Infrastructure.Docker;

/// <summary>Lese-Stream, der aus dem gemultiplexten Docker-Attach-Body fortlaufend die
/// stdout-Frames (Typ 1) demuxt und stderr (Typ 2) verwirft. Eigene Datei (statt privat
/// in SocketDockerClient verschachtelt), damit die Demux-Logik direkt testbar ist.</summary>
public sealed class DockerStdoutStream(Stream source) : Stream
{
    private byte[] _pending = [];
    private int _offset;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (_offset >= _pending.Length)
        {
            var frame = await DockerStreamDemux.ReadFrameAsync(source, ct);
            if (frame is null) return 0;                       // EOF
            if (frame.Value.StreamType == 2) continue;         // stderr verwerfen
            _pending = frame.Value.Payload; _offset = 0;
            if (_pending.Length == 0) continue;
        }
        var n = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
        _offset += n;
        return n;
    }

    public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
