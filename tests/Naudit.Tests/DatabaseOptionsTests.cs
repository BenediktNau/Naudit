using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

public class DatabaseOptionsTests
{
    [Fact]
    public void Defaults_disabled_sqlite_volumeDbPath()
    {
        var o = new DatabaseOptions();
        Assert.False(o.Enabled);
        Assert.Equal(DbProvider.Sqlite, o.Provider);
        Assert.Equal("Data Source=/data/naudit.db", o.ConnectionString);
    }
}
