using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;

namespace Naudit.Benchmark;

public static class BenchmarkHost
{
    /// <summary>Tauscht die zuletzt registrierten Nähte gegen ihre aufzeichnenden Dekoratoren.
    /// Muss NACH AddNauditInfrastructure laufen.
    /// <list type="bullet">
    /// <item><see cref="IGitPlatform"/> — Lesen geht an die echte Plattform, das Posten wird
    /// abgefangen; Checkout-Erfolg und -Fehlschlag werden getrennt vermerkt.</item>
    /// <item><see cref="IChatClient"/> — der Aufruf läuft unverändert durch; festgehalten wird,
    /// ob der Review-Prompt Repo-Kontext und Architektur-Profil trug (beides fail-open und
    /// ungeloggt) und was er an Tokens gekostet hat.</item>
    /// </list></summary>
    public static IServiceCollection AddBenchmarkCapture(this IServiceCollection services)
    {
        services.AddSingleton<ReviewCapture>();

        Decorate<IGitPlatform>(services,
            (real, sp) => new CapturingGitPlatform(real, sp.GetRequiredService<ReviewCapture>()));
        Decorate<IChatClient>(services,
            (real, sp) => new CapturingChatClient(real, sp.GetRequiredService<ReviewCapture>()));

        return services;
    }

    /// <summary>Letzte Registrierung suchen, entfernen, Fabrik umhüllen, Lebensdauer erhalten.
    /// Beide Nähte sind über eine ImplementationFactory registriert (Typed-HttpClient bzw.
    /// AiClientFactory). Fehlt die Fabrik, wird hart abgebrochen statt still nicht zu dekorieren —
    /// eine unbemerkt undekorierte Naht hieße: kein Abfangen des Postens bzw. keine Diagnose.</summary>
    private static void Decorate<TService>(
        IServiceCollection services, Func<TService, IServiceProvider, TService> wrap)
        where TService : class
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(TService))
            ?? throw new InvalidOperationException(
                $"Keine {typeof(TService).Name}-Registrierung gefunden — AddBenchmarkCapture muss nach AddNauditInfrastructure laufen.");

        if (existing.ImplementationFactory is null)
            throw new InvalidOperationException(
                $"{typeof(TService).Name} ist nicht über eine Fabrik registriert — die Dekoration müsste angepasst werden.");

        services.Remove(existing);
        services.Add(new ServiceDescriptor(
            typeof(TService),
            sp => wrap((TService)existing.ImplementationFactory(sp), sp),
            existing.Lifetime));
    }
}
