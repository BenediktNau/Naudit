using Naudit.Infrastructure.Process;
using Xunit;

namespace Naudit.Tests.Fakes;

internal static class AnalyzerAssertions
{
    // Analyzer laufen über Fremd-Code: sie dürfen nie die volle Host-Env (Naudit-Secrets) erben.
    public static void MinimalEnvironment(ProcessSpec spec)
    {
        Assert.NotNull(spec.InheritedEnvironment);
        Assert.DoesNotContain(spec.InheritedEnvironment!, e => e.StartsWith("Naudit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(spec.InheritedEnvironment!, e => e == "*");
        Assert.False(ProcessEnvironment.IsAllowed("Naudit__GitHub__Token", spec.InheritedEnvironment!));
    }
}
