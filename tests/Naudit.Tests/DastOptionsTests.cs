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
}
