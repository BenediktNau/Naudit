using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Naudit.Infrastructure.Docker;

/// <summary>IDockerClient über die Docker-Engine-HTTP-API am Unix-Socket. Bewusst ohne Docker.DotNet
/// (Supply-Chain-Härtung: kein neues NuGet) — SocketsHttpHandler mit ConnectCallback auf den
/// UnixDomainSocketEndPoint reicht für die wenigen benötigten Endpunkte. Transport-/API-Fehler
/// werden einheitlich als DockerUnavailableException gemeldet (Fail-Open-Naht des Runners).</summary>
public sealed class SocketDockerClient(string socketPath) : IDockerClient, IDisposable
{
    // für Wiring-Tests sichtbar: welcher Socket-Pfad gewann
    public string SocketPath { get; } = socketPath;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Docker-Engine-API erwartet PascalCase-Feldnamen (Image, Cmd, HostConfig, Binds, AttachStdin, …);
    // die Web-Defaults von JsonOpts würden beim Serialisieren camelCasen. Darum für ausgehende Bodies
    // eigene Options ohne NamingPolicy — Lesen (JsonOpts) bleibt case-insensitiv wie gehabt.
    private static readonly JsonSerializerOptions OutJsonOpts = new();

    // Kein Client-Timeout: lange Execs (claude-Review) laufen bis zum CancellationToken des Aufrufers.
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    })
    { BaseAddress = new Uri("http://docker"), Timeout = Timeout.InfiniteTimeSpan };

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/_ping", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false; // Ping ist die Sonde — nicht erreichbar ist ein Ergebnis, kein Fehler
        }
    }

    public async Task<string?> InspectSelfImageAsync(string hostname, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/containers/{hostname}/json"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(resp, ct);
        return (await ReadJsonAsync<InspectResponse>(resp, ct)).Image;
    }

    public async Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/containers/{name}/json"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(resp, ct);
        var body = await ReadJsonAsync<InspectResponse>(resp, ct);
        return new ContainerInfo(body.State?.Running ?? false);
    }

    public async Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default)
    {
        var hostConfig = new Dictionary<string, object?>();
        if (spec.VolumeName is not null && spec.VolumeTarget is not null)
            hostConfig["Binds"] = new[] { $"{spec.VolumeName}:{spec.VolumeTarget}" };
        if (spec.Network is not null)
            hostConfig["NetworkMode"] = spec.Network;
        if (spec.Limits is { } limits)
        {
            hostConfig["Memory"] = (long)limits.MemoryMb * 1024 * 1024;
            hostConfig["NanoCpus"] = (long)(limits.Cpus * 1_000_000_000);
            hostConfig["PidsLimit"] = limits.PidsLimit;
            hostConfig["CapDrop"] = new[] { "ALL" };
            hostConfig["SecurityOpt"] = new[] { "no-new-privileges" };
        }

        var create = new Dictionary<string, object?> { ["Image"] = spec.Image, ["HostConfig"] = hostConfig };
        if (spec.Command.Count > 0)
            create["Cmd"] = spec.Command;               // leer ⇒ CMD/ENTRYPOINT des Images gilt
        if (spec.Entrypoint is { Count: > 0 })
            create["Entrypoint"] = spec.Entrypoint;     // Probe-Container: ["sleep","infinity"]
        if (spec.Environment is { Count: > 0 })
            create["Env"] = spec.Environment.Select(kv => $"{kv.Key}={kv.Value}").ToArray();

        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"/containers/create?name={Uri.EscapeDataString(spec.Name)}")
        { Content = JsonContent.Create(create, options: OutJsonOpts) }, ct);
        // 409 = Name existiert bereits (Race zweier EnsureRunning) — dann genügt der Start.
        await EnsureAsync(resp, ct, HttpStatusCode.Conflict);
        await StartAsync(spec.Name, ct);
    }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/containers/{name}/start"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotModified); // 304 = lief schon
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/containers/{name}/stop?t=5"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotModified, HttpStatusCode.NotFound); // stand schon / weg
    }

    public async Task RemoveContainerAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/containers/{name}?force=true"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotFound);
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/volumes/{Uri.EscapeDataString(name)}"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotFound);
    }

    public async Task WriteFileAsync(string name, string directory, string fileName, string content, CancellationToken ct = default)
    {
        using var tarStream = new MemoryStream();
        await using (var tar = new TarWriter(tarStream, leaveOpen: true))
        {
            // 0644 statt 0600: der Tar-Eintrag gehört root, lesen muss ihn aber der non-root
            // Container-User (app). Inhalt ist der (bereits redigierte) Review-Prompt.
            var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            };
            await tar.WriteEntryAsync(entry, ct);
        }
        tarStream.Position = 0;
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/containers/{name}/archive?path={Uri.EscapeDataString(directory)}")
        { Content = new StreamContent(tarStream) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        using var resp = await SendAsync(req, ct);
        await EnsureAsync(resp, ct);
    }

    public async Task<DockerExecResult> ExecAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default)
    {
        var create = new
        {
            AttachStdin = false,
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            Env = environment?.Select(kv => $"{kv.Key}={kv.Value}").ToArray() ?? [],
            Cmd = argv,
            WorkingDir = workingDirectory,
        };
        using var createResp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/containers/{name}/exec")
        { Content = JsonContent.Create(create, options: OutJsonOpts) }, ct);
        await EnsureAsync(createResp, ct);
        var execId = (await ReadJsonAsync<ExecCreateResponse>(createResp, ct)).Id;

        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/exec/{execId}/start")
        { Content = JsonContent.Create(new { Detach = false, Tty = false }, options: OutJsonOpts) };
        // ResponseHeadersRead: der Body IST der (multiplexte) stdout/stderr-Stream des Execs.
        using var startResp = await SendAsync(startReq, ct, HttpCompletionOption.ResponseHeadersRead);
        await EnsureAsync(startResp, ct);
        string stdout, stderr;
        try
        {
            await using var stream = await startResp.Content.ReadAsStreamAsync(ct);
            (stdout, stderr) = await DockerStreamDemux.ReadAsync(stream, ct);
        }
        catch (IOException ex)
        {
            throw new DockerUnavailableException($"Docker-Exec-Stream abgerissen: {ex.Message}", ex);
        }

        using var inspectResp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/exec/{execId}/json"), ct);
        await EnsureAsync(inspectResp, ct);
        var inspect = await ReadJsonAsync<ExecInspectResponse>(inspectResp, ct);
        return new DockerExecResult(inspect.ExitCode, stdout, stderr);
    }

    public async Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(string namePrefix, CancellationToken ct = default)
    {
        // Docker-name-Filter matcht Substrings — Präfix wird darum unten client-seitig nachgeprüft.
        var filters = Uri.EscapeDataString($"{{\"name\":[\"{namePrefix}\"]}}");
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"/containers/json?all=true&filters={filters}"), ct);
        await EnsureAsync(resp, ct);
        var entries = await ReadJsonAsync<List<ListEntry>>(resp, ct);
        return entries
            .Select(e => new ContainerListEntry(
                (e.Names?.FirstOrDefault() ?? "").TrimStart('/'),
                string.Equals(e.State, "running", StringComparison.OrdinalIgnoreCase)))
            .Where(e => e.Name.StartsWith(namePrefix, StringComparison.Ordinal))
            .ToList();
    }

    public async Task CreateNetworkAsync(string name, CancellationToken ct = default)
    {
        // Internal = kein Egress. Kein Attachable: niemand hängt sich nachträglich hinein —
        // Container betreten das Netz beim Start, Naudit spricht nur über docker exec hinein.
        var body = new { Name = name, Internal = true, CheckDuplicate = true };
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "/networks/create")
        { Content = JsonContent.Create(body, options: OutJsonOpts) }, ct);
        await EnsureAsync(resp, ct, HttpStatusCode.Conflict); // 409 = gibt es schon
    }

    public async Task RemoveNetworkAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"/networks/{Uri.EscapeDataString(name)}"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotFound);
    }

    public async Task<IReadOnlyList<string>> ListNetworksAsync(string namePrefix, CancellationToken ct = default)
    {
        // Der name-Filter der Engine matcht Substrings — Präfix darum client-seitig nachprüfen
        // (gleiches Muster wie ListContainersAsync).
        var filters = Uri.EscapeDataString($"{{\"name\":[\"{namePrefix}\"]}}");
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/networks?filters={filters}"), ct);
        await EnsureAsync(resp, ct);
        var entries = await ReadJsonAsync<List<NetworkListEntry>>(resp, ct);
        return entries
            .Select(e => e.Name ?? "")
            .Where(n => n.StartsWith(namePrefix, StringComparison.Ordinal))
            .ToList();
    }

    public async Task<DockerBuildResult> BuildImageAsync(string tag, Stream tarContext, string dockerfilePath,
        CancellationToken ct = default)
    {
        var url = $"/build?t={Uri.EscapeDataString(tag)}&dockerfile={Uri.EscapeDataString(dockerfilePath)}" +
                  "&rm=true&forcerm=true&q=true";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StreamContent(tarContext) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        using var resp = await SendAsync(req, ct, HttpCompletionOption.ResponseHeadersRead);
        await EnsureAsync(resp, ct);

        // Der Body ist ein JSON-Lines-Strom; ein {"error":…}-Objekt darin bedeutet Build-Fehler,
        // der HTTP-Status bleibt dabei 200. Deshalb bis zum Ende lesen und auswerten.
        var log = new StringBuilder();
        var failed = false;
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
                continue;
            log.AppendLine(line);
            if (line.Contains("\"error\"", StringComparison.Ordinal))
                failed = true;
        }
        var text = log.ToString();
        return new DockerBuildResult(!failed, text.Length > 2000 ? text[^2000..] : text);
    }

    public async Task PullImageAsync(string reference, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"/images/create?fromImage={Uri.EscapeDataString(reference)}"), ct,
            HttpCompletionOption.ResponseHeadersRead);
        await EnsureAsync(resp, ct);

        // Auch hier JSON-Lines mit möglichem {"error":…} bei HTTP 200 — Strom leeren und prüfen.
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Contains("\"error\"", StringComparison.Ordinal))
                throw new DockerUnavailableException($"Image-Pull fehlgeschlagen: {reference}");
        }
    }

    public async Task RemoveImageAsync(string tag, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"/images/{Uri.EscapeDataString(tag)}?force=true"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotFound, HttpStatusCode.Conflict);
    }

    public async Task<IReadOnlyList<string>> ListImagesAsync(string tagPrefix, CancellationToken ct = default)
    {
        var filters = Uri.EscapeDataString($"{{\"reference\":[\"{tagPrefix}*\"]}}");
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/images/json?filters={filters}"), ct);
        await EnsureAsync(resp, ct);
        var entries = await ReadJsonAsync<List<ImageListEntry>>(resp, ct);
        return entries
            .SelectMany(e => e.RepoTags ?? [])
            .Where(t => t.StartsWith(tagPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public void Dispose() => _http.Dispose();

    // Transportfehler (Socket weg, Verbindung verweigert, …) einheitlich als DockerUnavailableException.
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await _http.SendAsync(req, completion, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            throw new DockerUnavailableException($"Docker-Socket '{SocketPath}' nicht nutzbar: {ex.Message}", ex);
        }
    }

    // API-Fehler (außer explizit akzeptierten Status) ebenfalls als DockerUnavailableException.
    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct, params HttpStatusCode[] accepted)
    {
        if (resp.IsSuccessStatusCode || accepted.Contains(resp.StatusCode))
            return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new DockerUnavailableException(
            $"Docker-API {(int)resp.StatusCode} bei {resp.RequestMessage?.RequestUri?.PathAndQuery}: {body}");
    }

    // Jeder Transport-/API-/Parse-Fehler läuft über DockerUnavailableException (Fail-Open-Naht) —
    // auch ein kaputter/unerwarteter 2xx-Body (JsonException) darf hier nicht roh durchschlagen.
    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct)
                   ?? throw new DockerUnavailableException($"Docker-API lieferte kein parsebares JSON ({typeof(T).Name}).");
        }
        catch (JsonException ex)
        {
            throw new DockerUnavailableException($"Docker-API lieferte kein parsebares JSON ({typeof(T).Name}).", ex);
        }
    }

    // Nur die benötigten Felder der Engine-Antworten.
    private sealed record InspectResponse(string? Image, InspectState? State);
    private sealed record InspectState(bool Running);
    private sealed record ListEntry(string[]? Names, string? State);
    private sealed record ExecCreateResponse(string Id);
    private sealed record ExecInspectResponse(int ExitCode, bool Running);
    private sealed record NetworkListEntry(string? Name);
    private sealed record ImageListEntry(List<string>? RepoTags);
}
