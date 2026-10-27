using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Entfernt das Pool-Opt-in (RoundRobin-Session-Routing ist ersatzlos gestrichen). Wie
    // AddSharePoolFlag bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter Typ). SQLite: EF baut
    // die Tabelle für DropColumn neu auf; Postgres: ALTER TABLE ... DROP COLUMN.
    /// <inheritdoc />
    public partial class RemoveSharePoolFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ShareSessionInPool", table: "Accounts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShareSessionInPool",
                table: "Accounts",
                nullable: false,
                defaultValue: false);
        }
    }
}
