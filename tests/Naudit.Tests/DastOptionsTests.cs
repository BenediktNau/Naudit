using Naudit.Infrastructure.Dast;
using Xunit;

namespace Naudit.Tests;

public class DastOptionsTests
{
    [Fact]
    public void AppliesTo_disabled_isFalse_evenForListedProject()
    {
        var options = new DastOptions { Enabled = false, Projects = { "acme/shop" } };

        Assert.False(options.AppliesTo("acme/shop"));
    }

    /// <summary>Leere Liste = kein Projekt (fail-closed): ein versehentlich global gesetzter
    /// Schalter führt so noch keinen fremden PR-Code aus.</summary>
    [Fact]
    public void AppliesTo_enabledButEmptyAllowlist_isFalse()
    {
        var options = new DastOptions { Enabled = true };

        Assert.False(options.AppliesTo("acme/shop"));
    }

    [Fact]
    public void AppliesTo_listedProject_isTrue_caseInsensitive_andTrimmed()
    {
        var options = new DastOptions { Enabled = true, Projects = { " Acme/Shop " } };

        Assert.True(options.AppliesTo("acme/shop"));
    }

    [Fact]
    public void AppliesTo_unlistedProject_isFalse()
    {
        var options = new DastOptions { Enabled = true, Projects = { "acme/shop" } };

        Assert.False(options.AppliesTo("acme/other"));
        Assert.False(options.AppliesTo(null));
    }

    /// <summary>Config-Binding kann null-Einträge in eine Liste binden — ein null/leerer Eintrag
    /// darf AppliesTo nicht mit einer NRE zum Absturz bringen, ein gültiger Eintrag muss trotzdem matchen.</summary>
    [Fact]
    public void AppliesTo_allowlistWithNullAndBlankEntries_stillMatchesValidEntry()
    {
        var options = new DastOptions { Enabled = true, Projects = { null!, "  ", "acme/shop" } };

        Assert.True(options.AppliesTo("acme/shop"));
    }

    [Fact]
    public void AppliesTo_allowlistWithOnlyNullAndBlankEntries_isFalse_noThrow()
    {
        var options = new DastOptions { Enabled = true, Projects = { null!, "  ", "" } };

        Assert.False(options.AppliesTo("acme/shop"));
    }

    [Fact]
    public void Defaults_probingKnobs()
    {
        var options = new DastOptions();

        Assert.Equal(12, options.MaxProbeSteps);
        Assert.Equal(
            new[] { "node", "/app/cli.js", "--headless", "--browser", "chromium", "--no-sandbox" },
            options.ProbeMcpArgv);
    }
}
