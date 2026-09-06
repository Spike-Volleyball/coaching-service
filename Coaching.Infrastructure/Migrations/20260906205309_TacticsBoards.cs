using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coaching.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TacticsBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TacticsFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TacticsFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TacticsBoards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    System = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClubId = table.Column<Guid>(type: "uuid", nullable: true),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false),
                    FrameCount = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Document = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TacticsBoards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TacticsBoards_TacticsFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "TacticsFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TacticsBoards_FolderId",
                table: "TacticsBoards",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_TacticsBoards_Scope_ClubId",
                table: "TacticsBoards",
                columns: new[] { "Scope", "ClubId" });

            migrationBuilder.CreateIndex(
                name: "IX_TacticsBoards_Scope_OwnerUserId",
                table: "TacticsBoards",
                columns: new[] { "Scope", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_TacticsBoards_Scope_TeamId",
                table: "TacticsBoards",
                columns: new[] { "Scope", "TeamId" });

            migrationBuilder.CreateIndex(
                name: "IX_TacticsFolders_Scope_ClubId",
                table: "TacticsFolders",
                columns: new[] { "Scope", "ClubId" });

            migrationBuilder.CreateIndex(
                name: "IX_TacticsFolders_Scope_OwnerUserId",
                table: "TacticsFolders",
                columns: new[] { "Scope", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_TacticsFolders_Scope_TeamId",
                table: "TacticsFolders",
                columns: new[] { "Scope", "TeamId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TacticsBoards");

            migrationBuilder.DropTable(
                name: "TacticsFolders");
        }
    }
}
