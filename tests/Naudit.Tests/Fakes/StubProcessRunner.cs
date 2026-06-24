using Naudit.Infrastructure.Process;

namespace Naudit.Tests.Fakes;

// Fängt den ProcessSpec ab und liefert eine vorgegebene Antwort (oder wirft).
internal sealed class StubProcessRunner(Func<ProcessSpec, ProcessResult> responder) : IProcessRunner
{
    public ProcessSpec? LastSpec { get; private set; }
    public List<ProcessSpec> Specs { get; } = new();

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        LastSpec = spec;
        Specs.Add(spec);
        return Task.FromResult(responder(spec)); // wirft responder, propagiert es RunAsync synchron
    }
}
