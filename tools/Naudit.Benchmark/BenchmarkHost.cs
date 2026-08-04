using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;

namespace Naudit.Benchmark;

public static class BenchmarkHost
{
    /// <summary>Tauscht die zuletzt registrierte IGitPlatform gegen den aufzeichnenden Dekorator.
    /// Muss NACH AddNauditInfrastructure laufen. Die echte Registrierung ist ein Typed-HttpClient,
    /// hat also eine ImplementationFactory — die rufen wir auf und umhüllen das Ergebnis.</summary>
    public static IServiceCollection AddBenchmarkCapture(this IServiceCollection services)
    {
        services.AddSingleton<ReviewCapture>();

        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IGitPlatform))
            ?? throw new InvalidOperationException(
                "Keine IGitPlatform-Registrierung gefunden — AddBenchmarkCapture muss nach AddNauditInfrastructure laufen.");

        if (existing.ImplementationFactory is null)
            throw new InvalidOperationException(
                "IGitPlatform ist nicht über eine Fabrik registriert — die Dekoration müsste angepasst werden.");

        services.Remove(existing);
        services.Add(new ServiceDescriptor(
            typeof(IGitPlatform),
            sp => new CapturingGitPlatform(
                (IGitPlatform)existing.ImplementationFactory(sp),
                sp.GetRequiredService<ReviewCapture>()),
            existing.Lifetime));

        return services;
    }
}
