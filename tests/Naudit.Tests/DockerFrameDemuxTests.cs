using Naudit.Infrastructure.Docker;
using Xunit;

namespace Naudit.Tests;

/// <summary>Charakterisierungstests für das Docker-Attach-Frame-Demuxing (DockerStreamDemux.ReadFrameAsync
/// + DockerStdoutStream) — bislang ungetesteter, aber tückischer Code: zerstückelte Socket-Reads,
/// stderr-Interleaving und Teil-Konsum über mehrere Frames hinweg.</summary>
public class DockerFrameDemuxTests
{
    /// <summary>Stream, der pro ReadAsync höchstens n Bytes liefert — simuliert zerstückelte
    /// Socket-Reads (ein Frame-Header kann über mehrere Reads kommen).</summary>
    private sealed class ChunkedStream(byte[] data, int maxPerRead) : Stream
    {
        private int _pos;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= data.Length) return ValueTask.FromResult(0);
            var n = Math.Min(Math.Min(maxPerRead, buffer.Length), data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return ValueTask.FromResult(n);
        }
        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => _pos = (int)value; }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>Baut einen Docker-Attach-Frame: 8-Byte-Header (Typ, 3x0, Big-Endian-Länge) + Payload.</summary>
    private static byte[] Frame(byte streamType, string payload)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(payload);
        var frame = new byte[8 + body.Length];
        frame[0] = streamType;
        frame[4] = (byte)(body.Length >> 24); frame[5] = (byte)(body.Length >> 16);
        frame[6] = (byte)(body.Length >> 8);  frame[7] = (byte)body.Length;
        body.CopyTo(frame, 8);
        return frame;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }

    private static async Task<string> ReadAllAsync(Stream stream, int bufferSize)
    {
        var sb = new System.Text.StringBuilder();
        var buf = new byte[bufferSize];
        int n;
        while ((n = await stream.ReadAsync(buf.AsMemory(0, bufferSize))) > 0)
            sb.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n));
        return sb.ToString();
    }

    /// <summary>Ein Frame-Header (8 Byte) kann über beliebig viele Socket-Reads verteilt ankommen —
    /// ReadFrameAsync muss ihn trotzdem korrekt zusammensetzen (1 Byte pro Read ist der Extremfall).</summary>
    [Fact]
    public async Task ReadFrameAsync_parsesSingleFrame_whenStreamDeliversOneByteAtATime()
    {
        var data = Frame(1, "hello");
        var stream = new ChunkedStream(data, maxPerRead: 1);

        var frame = await DockerStreamDemux.ReadFrameAsync(stream, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(1, frame!.Value.StreamType);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(frame.Value.Payload));
    }

    /// <summary>Sauberes Ende des Streams (kein einziges Byte mehr) ist kein Fehler, sondern EOF (null).</summary>
    [Fact]
    public async Task ReadFrameAsync_returnsNull_atCleanEof()
    {
        var stream = new ChunkedStream([], maxPerRead: 64);

        var frame = await DockerStreamDemux.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Null(frame);
    }

    /// <summary>Abriss mitten im 8-Byte-Header darf nicht werfen — der Aufrufer behandelt das wie EOF.</summary>
    [Fact]
    public async Task ReadFrameAsync_returnsNull_onEofMidHeader()
    {
        var stream = new ChunkedStream([1, 0, 0, 0], maxPerRead: 64); // nur 4 von 8 Header-Bytes

        var frame = await DockerStreamDemux.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Null(frame);
    }

    /// <summary>Abriss mitten im Payload (Header kündigt mehr Bytes an, als tatsächlich folgen)
    /// darf ebenfalls nicht werfen und liefert kein Garbage-Payload.</summary>
    [Fact]
    public async Task ReadFrameAsync_returnsNull_onEofMidPayload()
    {
        var header = new byte[] { 1, 0, 0, 0, 0, 0, 0, 10 }; // kündigt 10 Payload-Bytes an
        var partialPayload = System.Text.Encoding.UTF8.GetBytes("abc"); // nur 3 folgen
        var stream = new ChunkedStream(Concat(header, partialPayload), maxPerRead: 64);

        var frame = await DockerStreamDemux.ReadFrameAsync(stream, CancellationToken.None);

        Assert.Null(frame);
    }

    /// <summary>Ein Frame mit Länge 0 ist ein gültiger leerer Frame, kein EOF-Marker.</summary>
    [Fact]
    public async Task ReadFrameAsync_handlesZeroLengthFrame()
    {
        var stream = new ChunkedStream(Frame(1, ""), maxPerRead: 64);

        var frame = await DockerStreamDemux.ReadFrameAsync(stream, CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(1, frame!.Value.StreamType);
        Assert.Empty(frame.Value.Payload);
    }
}
