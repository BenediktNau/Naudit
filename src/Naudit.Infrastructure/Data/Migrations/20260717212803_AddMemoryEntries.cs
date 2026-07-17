using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Wie AddSharePoolFlag bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter Typ).
    /// <inheritdoc />
    public partial class AddMemoryEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MemoryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(nullable: false),
                    Kind = table.Column<string>(nullable: false),
                    File = table.Column<string>(nullable: true),
                    Text = table.Column<string>(nullable: false),
                    Reason = table.Column<string>(nullable: true),
                    SourceFindingId = table.Column<int>(nullable: true),
                    CreatedBy = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    Active = table.Column<bool>(nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEntries", x => x.Id);
                    table.ForeignKey("FK_MemoryEntries_Projects_ProjectId", x => x.ProjectId,
                        principalTable: "Projects", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey("FK_MemoryEntries_ReviewFindings_SourceFindingId", x => x.SourceFindingId,
                        principalTable: "ReviewFindings", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex("IX_MemoryEntries_ProjectId_Active", "MemoryEntries", new[] { "ProjectId", "Active" });
            migrationBuilder.CreateIndex("IX_MemoryEntries_SourceFindingId", "MemoryEntries", "SourceFindingId", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemoryEntries");
        }
    }
}
