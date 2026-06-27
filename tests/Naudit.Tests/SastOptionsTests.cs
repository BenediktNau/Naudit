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
}
