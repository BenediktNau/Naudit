using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Tests.Fakes;

/// <summary>In-Memory-SQLite-DbContext für Service-Tests. Die offene Verbindung hält die DB am Leben.</summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public NauditDbContext Context { get; }

    public TestDb()
    {
        _conn.Open();
        Context = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(_conn).Options);
        Context.Database.EnsureCreated();
    }

    public void Dispose() { Context.Dispose(); _conn.Dispose(); }
}
