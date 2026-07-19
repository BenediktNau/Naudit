using System.Buffers.Binary;
using System.Text;

namespace Naudit.Infrastructure.Docker;

/// <summary>Trennt den multiplexten Docker-Attach-Stream (8-Byte-Header: Typ, 3×0, Big-Endian-Länge)
/// in stdout (Typ 1; Typ 0 wird stdout zugeschlagen) und stderr (Typ 2). Abrupt endende Streams
/// (Abriss mitten im Frame) liefern das bis dahin Angekommene statt zu werfen.</summary>
public static class DockerStreamDemux
{
    public static async Task<(string StdOut, string StdErr)> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var header = new byte[8];
        while (true)
        {
            var read = await ReadUpToAsync(stream, header, ct);
            if (read < header.Length)
                break; // sauberes Ende (0) oder abgerissener Header — beides beendet den Stream
            var target = header[0] == 2 ? stderr : stdout;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (length == 0)
                continue;
            var payload = new byte[length];
            var got = await ReadUpToAsync(stream, payload, ct);
            target.Write(payload, 0, got);
            if (got < payload.Length)
                break; // Abriss mitten im Payload
        }
        return (Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    // Liest bis der Puffer voll ist oder der Stream endet; liefert die tatsächlich gelesenen Bytes.
    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n == 0)
                break;
            offset += n;
        }
        return offset;
    }
}
