using Naudit.Infrastructure.Ai.ClaudeCode;
using Xunit;

namespace Naudit.Tests;

public class SessionHealthRegistryTests
{
    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void UnknownAccount_isNotCoolingDown()
    {
        var registry = new SessionHealthRegistry();
        Assert.False(registry.IsCoolingDown(1));
        Assert.Null(registry.CoolingDownUntil(1));
    }

    [Fact]
    public void MarkFailure_coolsDown_untilWindowExpires()
    {
        var time = new TestTime();
        var registry = new SessionHealthRegistry(time);

        registry.MarkFailure(1, TimeSpan.FromMinutes(30));

        Assert.True(registry.IsCoolingDown(1));
        Assert.Equal(time.Now.AddMinutes(30), registry.CoolingDownUntil(1));

        time.Now = time.Now.AddMinutes(31);
        Assert.False(registry.IsCoolingDown(1));
        Assert.Null(registry.CoolingDownUntil(1));
    }

    [Fact]
    public void MarkFailure_otherAccount_isUnaffected()
    {
        var registry = new SessionHealthRegistry(new TestTime());
        registry.MarkFailure(1, TimeSpan.FromMinutes(30));
        Assert.False(registry.IsCoolingDown(2));
    }
}
