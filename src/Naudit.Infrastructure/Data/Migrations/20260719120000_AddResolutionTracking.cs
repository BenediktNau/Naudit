using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Provider-neutral handgepflegt (kein expliziter Typ), wie AddFindingCommentIds.
    /// <inheritdoc />
    public partial class AddResolutionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>("ResolutionStatus", "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<string>("ResolutionSource", "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<string>("ResolvedBy", "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<DateTime>("ResolvedAtUtc", "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<int>("TimesApplied", "MemoryEntries", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<DateTime>("LastAppliedAtUtc", "MemoryEntries", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn("ResolutionStatus", "ReviewFindings");
            migrationBuilder.DropColumn("ResolutionSource", "ReviewFindings");
            migrationBuilder.DropColumn("ResolvedBy", "ReviewFindings");
            migrationBuilder.DropColumn("ResolvedAtUtc", "ReviewFindings");
            migrationBuilder.DropColumn("TimesApplied", "MemoryEntries");
            migrationBuilder.DropColumn("LastAppliedAtUtc", "MemoryEntries");
        }
    }
}
