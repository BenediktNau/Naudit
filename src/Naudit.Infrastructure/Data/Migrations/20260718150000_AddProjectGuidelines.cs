using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Wie AddMemoryEntries bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter Typ).
    /// <inheritdoc />
    public partial class AddProjectGuidelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectGuidelines",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(nullable: false),
                    Markdown = table.Column<string>(nullable: false),
                    SourceHash = table.Column<string>(nullable: false),
                    DistilledAt = table.Column<DateTime>(nullable: false),
                    ManuallyEdited = table.Column<bool>(nullable: false),
                    SourcesChangedAt = table.Column<DateTime>(nullable: true),
                    UpdatedBy = table.Column<string>(nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectGuidelines", x => x.Id);
                    table.ForeignKey("FK_ProjectGuidelines_Projects_ProjectId", x => x.ProjectId,
                        principalTable: "Projects", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_ProjectGuidelines_ProjectId", "ProjectGuidelines", "ProjectId", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable(name: "ProjectGuidelines");
    }
}
