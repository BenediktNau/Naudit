using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

public class AppRestarterTests
{
    [Fact]
    public void ConsumeRestartRequest_liefertEinmalTrue_undResettet()
    {
        var r = new AppRestarter();
        Assert.False(r.ConsumeRestartRequest()); // ohne Request: kein Neustart
        r.RequestRestart();                       // ohne Attach: wirft nicht (Host evtl. noch nicht da)
        Assert.True(r.ConsumeRestartRequest());
        Assert.False(r.ConsumeRestartRequest());  // verbraucht
    }

    [Fact]
    public void MarkRestartPending_setztFlag()
    {
        var r = new AppRestarter();
        Assert.False(r.RestartPending);
        r.MarkRestartPending();
        Assert.True(r.RestartPending);
    }
}
