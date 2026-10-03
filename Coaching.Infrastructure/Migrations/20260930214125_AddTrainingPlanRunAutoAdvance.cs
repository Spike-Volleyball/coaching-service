using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coaching.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTrainingPlanRunAutoAdvance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoAdvance",
                table: "TrainingPlanRuns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_TrainingPlanRuns_AutoAdvancing",
                table: "TrainingPlanRuns",
                column: "CurrentItemStartedAtUtc",
                filter: "\"AutoAdvance\" AND \"Status\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrainingPlanRuns_AutoAdvancing",
                table: "TrainingPlanRuns");

            migrationBuilder.DropColumn(
                name: "AutoAdvance",
                table: "TrainingPlanRuns");
        }
    }
}
