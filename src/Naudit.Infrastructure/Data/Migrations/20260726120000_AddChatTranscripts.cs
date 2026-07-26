using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Wie AddProjectGuidelines/AddMemoryEntries bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter
    // Typ; der SQL-Generator liest den Spaltentyp aus dem TargetModel im .Designer). Bei einem künftigen
    // `dotnet ef migrations add` analog neutralisieren; der Snapshot bleibt SQLite-baked.
    /// <inheritdoc />
    public partial class AddChatTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Korrelation Review ⇄ Transcript (kein FK: die Transcripts entstehen vor der Review-Zeile).
            migrationBuilder.AddColumn<Guid>(
                name: "CorrelationId",
                table: "Reviews",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChatTranscripts",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CorrelationId = table.Column<Guid>(nullable: false),
                    ProjectId = table.Column<string>(nullable: false),
                    PrNumber = table.Column<int>(nullable: false),
                    Trigger = table.Column<string>(nullable: true),
                    Model = table.Column<string>(nullable: true),
                    SystemPrompt = table.Column<string>(nullable: true),
                    UserPrompt = table.Column<string>(nullable: true),
                    ResponseText = table.Column<string>(nullable: true),
                    InputTokens = table.Column<long>(nullable: true),
                    OutputTokens = table.Column<long>(nullable: true),
                    LatencyMs = table.Column<long>(nullable: false),
                    ToolCount = table.Column<int>(nullable: false),
                    Failed = table.Column<bool>(nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatTranscripts", x => x.Id);
                });

            migrationBuilder.CreateIndex("IX_ChatTranscripts_CorrelationId", "ChatTranscripts", "CorrelationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ChatTranscripts");
            migrationBuilder.DropColumn(name: "CorrelationId", table: "Reviews");
        }
    }
}
