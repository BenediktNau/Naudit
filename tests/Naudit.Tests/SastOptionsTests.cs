using Naudit.Infrastructure.Sast;
using Xunit;

namespace Naudit.Tests;

public class SastOptionsTests
{
    [Fact]
    public void ResolveOpengrepRules_withNoConfig_usesFullTreePlusOverlay()
    {
        var rules = SastOptions.ResolveOpengrepRules([]);

        // Voller gepinnter Baum (alle Sprachen) + eigenes Overlay — keine Sprach-Auswahl nötig.
        Assert.Equal(new[] { "/opt/opengrep-rules", "/opt/naudit-rules" }, rules);
    }

    [Fact]
    public void ResolveOpengrepRules_alwaysKeepsDefaults_thenAppendsConfigured_distinct()
    {
        var rules = SastOptions.ResolveOpengrepRules(["/opt/company-rules", "/opt/opengrep-rules"]);

        // Defaults bleiben IMMER erhalten (Overlay kann nie versehentlich wegfallen);
        // konfigurierte Pfade kommen additiv dazu, Duplikate dedupliziert.
        Assert.Equal(
            new[] { "/opt/opengrep-rules", "/opt/naudit-rules", "/opt/company-rules" },
            rules);
    }

    [Fact]
    public void ResolveAnalyzers_withEmptyConfig_usesDefaultPair()
    {
        var analyzers = SastOptions.ResolveAnalyzers([]);

        // Ohne Konfiguration greift derselbe Default wie in der DI-Registrierung.
        Assert.Equal(new[] { "opengrep", "trivy" }, analyzers);
    }

    [Fact]
    public void ResolveAnalyzers_withConfiguredList_returnsItUnchanged()
    {
        var analyzers = SastOptions.ResolveAnalyzers(["trivy", "osv-scanner"]);

        // Konfiguriert heißt konfiguriert — der Default ersetzt nichts und ergänzt nichts
        // (anders als ResolveOpengrepRules, wo das Overlay immer mitlaufen MUSS).
        Assert.Equal(new[] { "trivy", "osv-scanner" }, analyzers);
    }
}
