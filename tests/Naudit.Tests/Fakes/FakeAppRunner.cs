using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Dast;

namespace Naudit.Tests.Fakes;

/// <summary>Skriptbarer IAppRunner: zeichnet auf, ob RunAsync aufgerufen wurde, und liefert eine
/// RunningApp deren Teardown-Callback in Disposed protokolliert wird — oder null (ReturnNull),
/// um den „nicht anwendbar/kam nicht hoch"-Pfad zu testen.</summary>
internal sealed class FakeAppRunner : IAppRunner
{
    public bool RunCalled { get; private set; }
    public bool Disposed { get; private set; }
    public bool ReturnNull { get; set; }

    public Task<RunningApp?> RunAsync(IReviewWorkspace workspace, CancellationToken ct = default)
    {
        RunCalled = true;
        if (ReturnNull) return Task.FromResult<RunningApp?>(null);

        var app = new RunningApp("http://naudit-dast-app-x:8080/", "naudit-dast-net-x", "naudit-dast-app-x",
            "naudit-dast-pw-x", () => { Disposed = true; return ValueTask.CompletedTask; });
        return Task.FromResult<RunningApp?>(app);
    }
}
