using Naudit.Infrastructure.Ui;
using Xunit;

namespace Naudit.Tests;

public class UiOptionsTests
{
    [Fact]
    public void Defaults_disabled_noExternalAuth()
    {
        var o = new UiOptions();
        Assert.False(o.Enabled);
        Assert.False(o.Auth.GitHub.Enabled);
        Assert.False(o.Auth.Oidc.Enabled);
        Assert.Empty(o.Admins);
    }
}
