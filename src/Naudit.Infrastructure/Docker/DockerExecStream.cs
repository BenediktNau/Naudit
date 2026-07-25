namespace Naudit.Infrastructure.Docker;

/// <summary>Duplex-Kanal eines attached `docker exec`: Stdin (roh geschrieben) + Stdout (aus dem
/// gemultiplexten Docker-Stream heraus-demuxt). DisposeAsync schließt die zugrunde liegende
/// Socket-Verbindung. Für den MCP-Transport: Stdin = serverInput, Stdout = serverOutput.</summary>
public sealed class DockerExecStream(Stream stdin, Stream stdout, IAsyncDisposable underlying) : IAsyncDisposable
{
    public Stream Stdin { get; } = stdin;
    public Stream Stdout { get; } = stdout;

    public async ValueTask DisposeAsync() => await underlying.DisposeAsync();
}
