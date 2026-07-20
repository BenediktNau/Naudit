using System.Buffers.Binary;
using System.Text;
using Naudit.Infrastructure.Docker;
using Xunit;

namespace Naudit.Tests;

public class DockerStreamDemuxTests
{
    private static byte[] Frame(byte streamType, string payload)
    {
        var data = Encoding.UTF8.GetBytes(payload);
        var frame = new byte[8 + data.Length];
        frame[0] = streamType;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)data.Length);
        data.CopyTo(frame, 8);
        return frame;
    }

    [Fact]
    public async Task Splits_stdout_and_stderr_frames()
    {
        var stream = new MemoryStream([.. Frame(1, "out-1 "), .. Frame(2, "err-1"), .. Frame(1, "out-2")]);

        var (stdout, stderr) = await DockerStreamDemux.ReadAsync(stream);

        Assert.Equal("out-1 out-2", stdout);
        Assert.Equal("err-1", stderr);
    }

    [Fact]
    public async Task EmptyStream_yieldsEmptyOutputs()
    {
        var (stdout, stderr) = await DockerStreamDemux.ReadAsync(new MemoryStream());
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task TruncatedPayload_returnsWhatArrived()
    {
        var full = Frame(1, "hello");
        var truncated = full[..^2]; // Payload endet mitten im Frame (abrupter Verbindungsabriss)

        var (stdout, _) = await DockerStreamDemux.ReadAsync(new MemoryStream(truncated));

        Assert.Equal("hel", stdout);
    }

    [Fact]
    public async Task ZeroLengthFrame_isSkipped()
    {
        var stream = new MemoryStream([.. Frame(1, ""), .. Frame(2, "e")]);
        var (stdout, stderr) = await DockerStreamDemux.ReadAsync(stream);
        Assert.Equal("", stdout);
        Assert.Equal("e", stderr);
    }
}
