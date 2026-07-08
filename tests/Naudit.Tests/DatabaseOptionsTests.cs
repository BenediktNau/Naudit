using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

public class DatabaseOptionsTests
{
    [Fact]
    public void Defaults_sqlite_relativeDbPath()
    {
        var o = new DatabaseOptions();
        Assert.Equal(DbProvider.Sqlite, o.Provider);
        Assert.Equal("Data Source=data/naudit.db", o.ConnectionString);
    }
}
