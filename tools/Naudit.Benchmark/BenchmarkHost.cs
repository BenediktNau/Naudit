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
    /// <item><see cref="IWorkspaceProvider"/> — hält den tatsächlich ausgecheckten Commit fest
    /// (die Vorlage ist nicht eingefroren) und vermerkt einen gescheiterten Klon.</item>
    /// </list></summary>
    public static IServiceCollection AddBenchmarkCapture(this IServiceCollection services)
    {
        services.AddSingleton<ReviewCapture>();

        Decorate<IGitPlatform>(services,
            (real, sp) => new CapturingGitPlatform(real, sp.GetRequiredService<ReviewCapture>()));
        Decorate<IChatClient>(services,
            (real, sp) => new CapturingChatClient(real, sp.GetRequiredService<ReviewCapture>()));
        Decorate<IWorkspaceProvider>(services,
            (real, sp) => new CapturingWorkspaceProvider(real, sp.GetRequiredService<ReviewCapture>()));

        return services;
    }

    /// <summary>Letzte Registrierung suchen, entfernen, umhüllen, Lebensdauer erhalten. Deckt beide
    /// Registrierungsformen ab, die vorkommen: Fabrik (Typed-HttpClient, AiClientFactory) und
    /// konkreter Typ (AddScoped&lt;IWorkspaceProvider, GitWorkspaceProvider&gt;). Passt keine,
    /// wird hart abgebrochen statt still nicht zu dekorieren — eine unbemerkt undekorierte Naht
    /// hieße: kein Abfangen des Postens bzw. eine blinde Diagnose.</summary>
    private static void Decorate<TService>(
        IServiceCollection services, Func<TService, IServiceProvider, TService> wrap)
        where TService : class
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(TService))
            ?? throw new InvalidOperationException(
                $"Keine {typeof(TService).Name}-Registrierung gefunden — AddBenchmarkCapture muss nach AddNauditInfrastructure laufen.");

        Func<IServiceProvider, TService> resolveReal;
        if (existing.ImplementationFactory is { } factory)
        {
            resolveReal = sp => (TService)factory(sp);
        }
        else if (existing.ImplementationType is { } implementationType)
        {
            // Den konkreten Typ zusätzlich unter sich selbst registrieren, damit der Container ihn
            // weiterhin selbst baut (inkl. aller Ctor-Abhängigkeiten) — wir holen ihn nur ab.
            services.Add(new ServiceDescriptor(implementationType, implementationType, existing.Lifetime));
            resolveReal = sp => (TService)sp.GetRequiredService(implementationType);
        }
        else
        {
            throw new InvalidOperationException(
                $"{typeof(TService).Name} ist weder über eine Fabrik noch über einen konkreten Typ registriert — die Dekoration müsste angepasst werden.");
        }

        services.Remove(existing);
        services.Add(new ServiceDescriptor(
            typeof(TService),
            sp => wrap(resolveReal(sp), sp),
            existing.Lifetime));
    }
}
