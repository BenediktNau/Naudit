using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Naudit.Infrastructure.Data;

/// <summary>Design-Time-Factory für `dotnet ef migrations add` — zur Laufzeit ungenutzt.</summary>
public sealed class NauditDbContextFactory : IDesignTimeDbContextFactory<NauditDbContext>
{
    public NauditDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
