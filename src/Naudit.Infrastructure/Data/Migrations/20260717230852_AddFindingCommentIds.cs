using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Wie AddSharePoolFlag bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter Typ).
    /// <inheritdoc />
    public partial class AddFindingCommentIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "PlatformCommentId", table: "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PlatformNoteId", table: "ReviewFindings", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlatformCommentId",
                table: "ReviewFindings");

            migrationBuilder.DropColumn(
                name: "PlatformNoteId",
                table: "ReviewFindings");
        }
    }
}
