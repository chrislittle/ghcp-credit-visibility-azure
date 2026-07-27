using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class MultiEnterprise : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageSnapshots_Year_Month_Day_UserLogin_Model_Sku",
                table: "UsageSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_PrincipalCostCenterMappings_PrincipalType_PrincipalObjectId_CostCenterId",
                table: "PrincipalCostCenterMappings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CostCenterDirectory",
                table: "CostCenterDirectory");

            migrationBuilder.DropIndex(
                name: "IX_BudgetSnapshots_Scope_CostCenterId",
                table: "BudgetSnapshots");

            migrationBuilder.AddColumn<long>(
                name: "EnterpriseId",
                table: "UsageSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "EnterpriseId",
                table: "SnapshotRuns",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "EnterpriseId",
                table: "PrincipalCostCenterMappings",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "EnterpriseId",
                table: "CostCenterDirectory",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "EnterpriseId",
                table: "BudgetSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddPrimaryKey(
                name: "PK_CostCenterDirectory",
                table: "CostCenterDirectory",
                columns: new[] { "EnterpriseId", "CostCenterId" });

            migrationBuilder.CreateTable(
                name: "Enterprises",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slug = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PatSecretName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    UseMockData = table.Column<bool>(type: "bit", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSnapshotUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Enterprises", x => x.Id);
                });

            // Seed the bootstrap enterprise as row #1 — every pre-multi-enterprise row was
            // backfilled to EnterpriseId = 1 by the column defaults above, so existing
            // single-enterprise deployments upgrade losslessly. A migration cannot read app
            // configuration, so the slug is a placeholder; EnterpriseRegistryService replaces it
            // with the GitHub:Enterprise value (and the UseMock flag) on first startup.
            migrationBuilder.InsertData(
                table: "Enterprises",
                columns: new[] { "Id", "Slug", "DisplayName", "PatSecretName", "UseMockData", "Enabled", "CreatedUtc", "LastSnapshotUtc", "ModifiedBy" },
                values: new object[] { 1L, "__bootstrap__", "Default enterprise", "github-pat", false, true, new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc), null, "migration" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageSnapshots_EnterpriseId",
                table: "UsageSnapshots",
                column: "EnterpriseId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageSnapshots_EnterpriseId_Year_Month_Day_UserLogin_Model_Sku",
                table: "UsageSnapshots",
                columns: new[] { "EnterpriseId", "Year", "Month", "Day", "UserLogin", "Model", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRuns_EnterpriseId_StartedUtc",
                table: "SnapshotRuns",
                columns: new[] { "EnterpriseId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PrincipalCostCenterMappings_PrincipalType_PrincipalObjectId_EnterpriseId_CostCenterId",
                table: "PrincipalCostCenterMappings",
                columns: new[] { "PrincipalType", "PrincipalObjectId", "EnterpriseId", "CostCenterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetSnapshots_EnterpriseId_Scope_CostCenterId",
                table: "BudgetSnapshots",
                columns: new[] { "EnterpriseId", "Scope", "CostCenterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Enterprises_Slug",
                table: "Enterprises",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Enterprises");

            migrationBuilder.DropIndex(
                name: "IX_UsageSnapshots_EnterpriseId",
                table: "UsageSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_UsageSnapshots_EnterpriseId_Year_Month_Day_UserLogin_Model_Sku",
                table: "UsageSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_SnapshotRuns_EnterpriseId_StartedUtc",
                table: "SnapshotRuns");

            migrationBuilder.DropIndex(
                name: "IX_PrincipalCostCenterMappings_PrincipalType_PrincipalObjectId_EnterpriseId_CostCenterId",
                table: "PrincipalCostCenterMappings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CostCenterDirectory",
                table: "CostCenterDirectory");

            migrationBuilder.DropIndex(
                name: "IX_BudgetSnapshots_EnterpriseId_Scope_CostCenterId",
                table: "BudgetSnapshots");

            migrationBuilder.DropColumn(
                name: "EnterpriseId",
                table: "UsageSnapshots");

            migrationBuilder.DropColumn(
                name: "EnterpriseId",
                table: "SnapshotRuns");

            migrationBuilder.DropColumn(
                name: "EnterpriseId",
                table: "PrincipalCostCenterMappings");

            migrationBuilder.DropColumn(
                name: "EnterpriseId",
                table: "CostCenterDirectory");

            migrationBuilder.DropColumn(
                name: "EnterpriseId",
                table: "BudgetSnapshots");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CostCenterDirectory",
                table: "CostCenterDirectory",
                column: "CostCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageSnapshots_Year_Month_Day_UserLogin_Model_Sku",
                table: "UsageSnapshots",
                columns: new[] { "Year", "Month", "Day", "UserLogin", "Model", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrincipalCostCenterMappings_PrincipalType_PrincipalObjectId_CostCenterId",
                table: "PrincipalCostCenterMappings",
                columns: new[] { "PrincipalType", "PrincipalObjectId", "CostCenterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetSnapshots_Scope_CostCenterId",
                table: "BudgetSnapshots",
                columns: new[] { "Scope", "CostCenterId" },
                unique: true);
        }
    }
}
