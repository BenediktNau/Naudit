using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // HINWEIS: Wie InitialUi bewusst PROVIDER-NEUTRAL handgepflegt (keine expliziten
    // Spaltentypen, beide Identity-Strategien annotiert) — Begründung s. 20260707170820_InitialUi.cs.
    /// <inheritdoc />
    public partial class AuthorSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiSessionAccountId",
                table: "Reviews",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaudeSessionToken",
                table: "Accounts",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaudeSessionUpdatedAtUtc",
                table: "Accounts",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitAuthorLogin",
                table: "Accounts",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_AiSessionAccountId",
                table: "Reviews",
                column: "AiSessionAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Reviews_Accounts_AiSessionAccountId",
                table: "Reviews",
                column: "AiSessionAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Reviews_Accounts_AiSessionAccountId",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_AiSessionAccountId",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "AiSessionAccountId",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "ClaudeSessionToken",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ClaudeSessionUpdatedAtUtc",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "GitAuthorLogin",
                table: "Accounts");
        }
    }
}
