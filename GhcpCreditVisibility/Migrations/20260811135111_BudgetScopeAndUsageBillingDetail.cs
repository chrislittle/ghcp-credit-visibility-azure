using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class BudgetScopeAndUsageBillingDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BudgetSnapshots_EnterpriseId_Scope_CostCenterId",
                table: "BudgetSnapshots");

            // REQUIRED — without this the migration FAILS and the deployment rolls back.
            // GitHubBudgetId is added below with a "" default, so every pre-existing row receives
            // the same value; the UNIQUE index created at the end of this migration then violates
            // on any enterprise holding more than one budget row (one Org + N CostCenter rows —
            // i.e. every real deployment).
            //
            // Clearing the table is safe and is the intended path: BudgetSnapshots is a pure
            // read-through cache of GitHub-governed state. Nothing is authored in-app, nothing is
            // unrecoverable — the snapshot job re-fetches every budget in full each cycle and
            // already deletes rows GitHub no longer reports, so the table repopulates correctly
            // keyed on the first run after deploy.
            //
            // NOTE the asymmetry with the UsageSnapshots columns added below: usage history CANNOT
            // be refetched (GitHub serves the current month only), which is why those columns are
            // added non-destructively and nullable. Budgets can be refetched; usage cannot.
            //
            // Trade-off accepted: the Budgets page is empty between this migration and the first
            // snapshot run. A brief empty state beats a failed deploy, and beats the pre-fix state
            // where four scopes shared one row and the survivor was shown as the enterprise budget.
            migrationBuilder.Sql("DELETE FROM [BudgetSnapshots];");

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountAmount",
                table: "UsageSnapshots",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountQuantity",
                table: "UsageSnapshots",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GrossQuantity",
                table: "UsageSnapshots",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PricePerUnit",
                table: "UsageSnapshots",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Scope",
                table: "BudgetSnapshots",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<string>(
                name: "EntityName",
                table: "BudgetSnapshots",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GitHubBudgetId",
                table: "BudgetSnapshots",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "PreventFurtherUsage",
                table: "BudgetSnapshots",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UserLogin",
                table: "BudgetSnapshots",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetSnapshots_EnterpriseId_GitHubBudgetId",
                table: "BudgetSnapshots",
                columns: new[] { "EnterpriseId", "GitHubBudgetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetSnapshots_EnterpriseId_Scope",
                table: "BudgetSnapshots",
                columns: new[] { "EnterpriseId", "Scope" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BudgetSnapshots_EnterpriseId_GitHubBudgetId",
                table: "BudgetSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_BudgetSnapshots_EnterpriseId_Scope",
                table: "BudgetSnapshots");

            migrationBuilder.DropColumn(
                name: "DiscountAmount",
                table: "UsageSnapshots");

            migrationBuilder.DropColumn(
                name: "DiscountQuantity",
                table: "UsageSnapshots");

            migrationBuilder.DropColumn(
                name: "GrossQuantity",
                table: "UsageSnapshots");

            migrationBuilder.DropColumn(
                name: "PricePerUnit",
                table: "UsageSnapshots");

            migrationBuilder.DropColumn(
                name: "EntityName",
                table: "BudgetSnapshots");

            migrationBuilder.DropColumn(
                name: "GitHubBudgetId",
                table: "BudgetSnapshots");

            migrationBuilder.DropColumn(
                name: "PreventFurtherUsage",
                table: "BudgetSnapshots");

            migrationBuilder.DropColumn(
                name: "UserLogin",
                table: "BudgetSnapshots");

            migrationBuilder.AlterColumn<string>(
                name: "Scope",
                table: "BudgetSnapshots",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetSnapshots_EnterpriseId_Scope_CostCenterId",
                table: "BudgetSnapshots",
                columns: new[] { "EnterpriseId", "Scope", "CostCenterId" },
                unique: true);
        }
    }
}
