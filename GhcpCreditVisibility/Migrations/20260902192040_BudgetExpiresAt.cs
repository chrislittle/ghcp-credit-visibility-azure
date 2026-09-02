using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class BudgetExpiresAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Purely additive; no backfill needed and none possible.
            //
            // NULL here does NOT carry the "not captured" meaning the nullable columns added by
            // BudgetScopeAndUsageBillingDetail do — NULL is GitHub's own answer for "this budget
            // does not expire", which is the default and was the only behavior before 2026-09-01.
            // The ambiguity that would otherwise create for pre-existing rows resolves itself:
            // BudgetSnapshots is a read-through cache that the snapshot job refetches IN FULL every
            // cycle, so every row carries a genuine value after the first run post-deploy.
            //
            // "date" rather than "datetime2": GitHub sends YYYY-MM-DD, and a calendar day given a
            // UTC time would land on the wrong day for anyone east of Greenwich.
            migrationBuilder.AddColumn<DateOnly>(
                name: "ExpiresAt",
                table: "BudgetSnapshots",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "BudgetSnapshots");
        }
    }
}
