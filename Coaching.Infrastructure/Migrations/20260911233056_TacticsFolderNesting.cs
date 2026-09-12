using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coaching.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TacticsFolderNesting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentFolderId",
                table: "TacticsFolders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "TacticsFolders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_TacticsFolders_ParentFolderId_Position",
                table: "TacticsFolders",
                columns: new[] { "ParentFolderId", "Position" });

            migrationBuilder.AddForeignKey(
                name: "FK_TacticsFolders_TacticsFolders_ParentFolderId",
                table: "TacticsFolders",
                column: "ParentFolderId",
                principalTable: "TacticsFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TacticsFolders_TacticsFolders_ParentFolderId",
                table: "TacticsFolders");

            migrationBuilder.DropIndex(
                name: "IX_TacticsFolders_ParentFolderId_Position",
                table: "TacticsFolders");

            migrationBuilder.DropColumn(
                name: "ParentFolderId",
                table: "TacticsFolders");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "TacticsFolders");
        }
    }
}
