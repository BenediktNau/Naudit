using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Naudit.Infrastructure.Data;

public sealed class NauditDbContext(DbContextOptions<NauditDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<GitHubLinkEntity> GitHubLinks => Set<GitHubLinkEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<ReviewEntity> Reviews => Set<ReviewEntity>();
    public DbSet<ReviewFindingEntity> ReviewFindings => Set<ReviewFindingEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<SetupDraftEntity> SetupDrafts => Set<SetupDraftEntity>();
    public DbSet<MemoryEntryEntity> MemoryEntries => Set<MemoryEntryEntity>();
    public DbSet<ProjectGuidelinesEntity> ProjectGuidelines => Set<ProjectGuidelinesEntity>();

    /// <summary>Data-Protection-Keys (Session-Cookie-Signatur) — in der DB statt im Dateisystem,
    /// damit Sessions Container-Neustarts auf beiden Backends überleben.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AccountEntity>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            // Externe Identität eindeutig: verhindert doppelte Accounts bei parallelen OAuth/OIDC-Callbacks.
            // ExternalId ist bei lokalen Accounts null; NULLs gelten (SQLite wie Postgres) als verschieden,
            // mehrere lokale Accounts kollidieren also nicht.
            e.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();
            e.Property(x => x.Status).HasConversion<string>();     // lesbare Werte in SQLite
            e.Property(x => x.Provider).HasConversion<string>();
        });
        b.Entity<GitHubLinkEntity>(e =>
        {
            e.HasIndex(x => new { x.AccountId, x.Login }).IsUnique();
            e.HasOne(x => x.Account).WithMany(a => a.GitHubLinks)
                .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProjectEntity>(e => e.HasIndex(x => x.PlatformProjectId).IsUnique());
        b.Entity<ReviewEntity>(e =>
        {
            e.HasIndex(x => x.CreatedAt);                          // Dashboard-Zeitreihen
            e.HasOne(x => x.Project).WithMany(p => p.Reviews)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // Attribution ohne Navigation: Account weg ⇒ Review bleibt, Zuordnung wird null.
            e.HasOne<AccountEntity>().WithMany()
                .HasForeignKey(x => x.AiSessionAccountId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ReviewFindingEntity>(e =>
            e.HasOne(x => x.Review).WithMany(r => r.Findings)
                .HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Cascade));
        b.Entity<MemoryEntryEntity>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Active });        // Selektion pro Projekt
            // NULLs kollidieren nicht (SQLite wie Postgres) — Konventionen haben kein SourceFinding.
            e.HasIndex(x => x.SourceFindingId).IsUnique();
            e.HasOne(x => x.Project).WithMany()
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // Finding gelöscht (Review-Kaskade) ⇒ Eintrag bleibt, nur der Anker wird null.
            e.HasOne<ReviewFindingEntity>().WithMany()
                .HasForeignKey(x => x.SourceFindingId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ProjectGuidelinesEntity>(e =>
        {
            e.HasIndex(x => x.ProjectId).IsUnique();     // genau ein Profil pro Projekt
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<SettingEntity>(e => e.HasKey(x => x.Key));
        // Id wird von der App gesetzt (immer 1) — kein Autoincrement, hält die Migration provider-neutral.
        b.Entity<SetupDraftEntity>(e => e.Property(x => x.Id).ValueGeneratedNever());
    }
}
