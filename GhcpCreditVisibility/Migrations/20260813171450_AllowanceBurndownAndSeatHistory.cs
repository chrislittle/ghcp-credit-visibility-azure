using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class AllowanceBurndownAndSeatHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EnterpriseCopilotSeats_EnterpriseId_PlanType",
                table: "EnterpriseCopilotSeats");

            migrationBuilder.AddColumn<int>(
                name: "Month",
                table: "EnterpriseCopilotSeats",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Year",
                table: "EnterpriseCopilotSeats",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "GrossQuantity",
                table: "DailyUsageSnapshots",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            // Stamp pre-existing rows with the month they were actually captured in. Without this
            // they keep the 0/0 default, which no read ever matches — so they would sit in the table
            // forever as invisible garbage, never returned and never replaced (the write only
            // overwrites the CURRENT month). SnapshotUtc records when the count was taken, so this
            // is a truthful backfill rather than a guess.
            migrationBuilder.Sql(@"
UPDATE [EnterpriseCopilotSeats]
SET [Year] = YEAR([SnapshotUtc]), [Month] = MONTH([SnapshotUtc])
WHERE [Year] = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_EnterpriseCopilotSeats_EnterpriseId_PlanType_Year_Month",
                table: "EnterpriseCopilotSeats",
                columns: new[] { "EnterpriseId", "PlanType", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnterpriseCopilotSeats_EnterpriseId_Year_Month",
                table: "EnterpriseCopilotSeats",
                columns: new[] { "EnterpriseId", "Year", "Month" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EnterpriseCopilotSeats_EnterpriseId_PlanType_Year_Month",
                table: "EnterpriseCopilotSeats");

            migrationBuilder.DropIndex(
                name: "IX_EnterpriseCopilotSeats_EnterpriseId_Year_Month",
                table: "EnterpriseCopilotSeats");

            migrationBuilder.DropColumn(
                name: "Month",
                table: "EnterpriseCopilotSeats");

            migrationBuilder.DropColumn(
                name: "Year",
                table: "EnterpriseCopilotSeats");

            migrationBuilder.DropColumn(
                name: "GrossQuantity",
                table: "DailyUsageSnapshots");

            migrationBuilder.CreateIndex(
                name: "IX_EnterpriseCopilotSeats_EnterpriseId_PlanType",
                table: "EnterpriseCopilotSeats",
                columns: new[] { "EnterpriseId", "PlanType" },
                unique: true);
        }
    }
}
