using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // HINWEIS: Wie InitialUi/AddDataProtectionKeys bewusst PROVIDER-NEUTRAL handgepflegt (keine
    // expliziten Spaltentypen) — Begründung s. 20260707170820_InitialUi.cs.
    /// <inheritdoc />
    public partial class AddSettingsAndSetupDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(nullable: false),
                    Value = table.Column<string>(nullable: false),
                    IsSecret = table.Column<bool>(nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SetupDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false),
                    Json = table.Column<string>(nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SetupDrafts", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SetupDrafts");
        }
    }
}
