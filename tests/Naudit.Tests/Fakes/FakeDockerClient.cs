using Naudit.Infrastructure.Docker;

namespace Naudit.Tests.Fakes;

/// <summary>Skriptbarer IDockerClient: Container/Volumes in-memory, zeichnet Aufrufe auf und kann
/// gezielt DockerUnavailableException werfen (FailNextExecs) oder Execs verzögern (ExecDelay).</summary>
internal class FakeDockerClient : IDockerClient
{
    public Dictionary<string, bool> Containers { get; } = new();   // Name -> Running
    public HashSet<string> Volumes { get; } = new();
    public HashSet<string> Networks { get; } = new();
    public List<string> Calls { get; } = new();
    public List<(string Container, string Path, string Content)> WrittenFiles { get; } = new();
    public List<(string Container, IReadOnlyList<string> Argv, IReadOnlyDictionary<string, string?>? Env, string WorkingDir)> Execs { get; } = new();
    public Queue<DockerExecResult> ExecResults { get; } = new();   // leer ⇒ Exit 0, ""/""
    public bool PingResult { get; set; } = true;
    public string? SelfImage { get; set; } = "sha256:self-image";
    public int FailNextExecs { get; set; }
    public TimeSpan? ExecDelay { get; set; }
    public TimeSpan? RunDelay { get; set; }   // simuliert einen langsamen `docker run` (Concurrency-Tests)
    public ContainerRunSpec? LastRunSpec { get; private set; }

    public Task<bool> PingAsync(CancellationToken ct = default)
    {
        Calls.Add("ping");
        return Task.FromResult(PingResult);
    }

    public Task<string?> InspectSelfImageAsync(string hostname, CancellationToken ct = default)
    {
        Calls.Add($"inspect-self:{hostname}");
        return Task.FromResult(SelfImage);
    }

    public Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Containers.TryGetValue(name, out var running) ? new ContainerInfo(running) : null);

    public async Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default)
    {
        Calls.Add($"run:{spec.Name}");
        LastRunSpec = spec;
        if (RunDelay is { } d)
            await Task.Delay(d, ct);
        Containers[spec.Name] = true;
        Volumes.Add(spec.VolumeName);
    }

    public Task StartAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"start:{name}");
        Containers[name] = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"stop:{name}");
        if (Containers.ContainsKey(name)) Containers[name] = false;
        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"rm:{name}");
        Containers.Remove(name);
        return Task.CompletedTask;
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"rmvol:{name}");
        Volumes.Remove(name);
        return Task.CompletedTask;
    }

    public Task WriteFileAsync(string name, string directory, string fileName, string content, CancellationToken ct = default)
    {
        WrittenFiles.Add((name, $"{directory}/{fileName}", content));
        return Task.CompletedTask;
    }

    public async Task<DockerExecResult> ExecAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default)
    {
        Execs.Add((name, argv, environment, workingDirectory));
        if (FailNextExecs > 0)
        {
            FailNextExecs--;
            throw new DockerUnavailableException("fake: exec down");
        }
        if (ExecDelay is { } delay)
            await Task.Delay(delay, ct);
        return ExecResults.Count > 0 ? ExecResults.Dequeue() : new DockerExecResult(0, "", "");
    }

    public Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(string namePrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContainerListEntry>>(Containers
            .Where(c => c.Key.StartsWith(namePrefix, StringComparison.Ordinal))
            .Select(c => new ContainerListEntry(c.Key, c.Value))
            .ToList());

    public virtual Task CreateNetworkAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"netcreate:{name}");
        Networks.Add(name);
        return Task.CompletedTask;
    }

    public Task RemoveNetworkAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"netrm:{name}");
        Networks.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListNetworksAsync(string namePrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Networks
            .Where(n => n.StartsWith(namePrefix, StringComparison.Ordinal)).ToList());
}
