using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coaching.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TacticsBoardOriginAndDrawing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DrawnAt",
                table: "TacticsBoards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "TacticsBoards",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DrawnAt",
                table: "TacticsBoards");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "TacticsBoards");
        }
    }
}
