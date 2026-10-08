using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coaching.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TacticsBoardShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShareToken",
                table: "TacticsBoards",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SharedDocument",
                table: "TacticsBoards",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TacticsBoards_ShareToken",
                table: "TacticsBoards",
                column: "ShareToken",
                unique: true,
                filter: "\"ShareToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TacticsBoards_ShareToken",
                table: "TacticsBoards");

            migrationBuilder.DropColumn(
                name: "ShareToken",
                table: "TacticsBoards");

            migrationBuilder.DropColumn(
                name: "SharedDocument",
                table: "TacticsBoards");
        }
    }
}
