using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialUi : Migration
    {
        // HINWEIS: Diese Migration ist bewusst PROVIDER-NEUTRAL handgepflegt, damit dasselbe
        // Schema auf SQLite UND Postgres läuft (Naudit:Ui:DbProvider). Konkret:
        //  - keine expliziten `type:` (jeder Provider wählt seinen Default: TEXT→text,
        //    INTEGER→integer/bigint/boolean, DateTime→timestamptz);
        //  - auf jeder PK-Id BEIDE Identity-Strategien annotiert — jeder Provider nutzt seine
        //    und ignoriert die des anderen.
        // Bei einem künftigen `dotnet ef migrations add` re-baked EF wieder provider-spezifisch;
        // die neue Migration dann analog neutralisieren.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(nullable: false),
                    PasswordHash = table.Column<string>(nullable: true),
                    Provider = table.Column<string>(nullable: false),
                    ExternalId = table.Column<string>(nullable: true),
                    DisplayName = table.Column<string>(nullable: true),
                    Status = table.Column<string>(nullable: false),
                    IsAdmin = table.Column<bool>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlatformProjectId = table.Column<string>(nullable: false),
                    AccountId = table.Column<int>(nullable: true),
                    FirstReviewedAt = table.Column<DateTime>(nullable: false),
                    LastReviewedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GitHubLinks",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountId = table.Column<int>(nullable: false),
                    Login = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitHubLinks_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reviews",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(nullable: false),
                    PrNumber = table.Column<int>(nullable: false),
                    Title = table.Column<string>(nullable: false),
                    Verdict = table.Column<string>(nullable: false),
                    Summary = table.Column<string>(nullable: false),
                    InputTokens = table.Column<long>(nullable: true),
                    OutputTokens = table.Column<long>(nullable: true),
                    Model = table.Column<string>(nullable: true),
                    CreatedAt = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reviews_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewFindings",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReviewId = table.Column<int>(nullable: false),
                    Severity = table.Column<string>(nullable: false),
                    Confidence = table.Column<string>(nullable: false),
                    File = table.Column<string>(nullable: true),
                    Line = table.Column<int>(nullable: true),
                    Text = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReviewFindings_Reviews_ReviewId",
                        column: x => x.ReviewId,
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Username",
                table: "Accounts",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Provider_ExternalId",
                table: "Accounts",
                columns: new[] { "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubLinks_AccountId_Login",
                table: "GitHubLinks",
                columns: new[] { "AccountId", "Login" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_PlatformProjectId",
                table: "Projects",
                column: "PlatformProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewFindings_ReviewId",
                table: "ReviewFindings",
                column: "ReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_CreatedAt",
                table: "Reviews",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_ProjectId",
                table: "Reviews",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitHubLinks");

            migrationBuilder.DropTable(
                name: "ReviewFindings");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Reviews");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
