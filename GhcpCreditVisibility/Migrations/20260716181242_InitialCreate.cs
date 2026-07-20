using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminPrincipals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PrincipalType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PrincipalObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PrincipalDisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminPrincipals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "BudgetSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CostCenterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CostCenterName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ConsumedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SnapshotUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrincipalCostCenterMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PrincipalType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PrincipalObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PrincipalDisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CostCenterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CostCenterName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrincipalCostCenterMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowsWritten = table.Column<int>(type: "int", nullable: false),
                    RowsPurged = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SnapshotUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    UserLogin = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    CostCenterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CostCenterName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Product = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    NetQuantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    NetAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    GrossAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminPrincipals_PrincipalType_PrincipalObjectId",
                table: "AdminPrincipals",
                columns: new[] { "PrincipalType", "PrincipalObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BudgetSnapshots_Scope_CostCenterId",
                table: "BudgetSnapshots",
                columns: new[] { "Scope", "CostCenterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrincipalCostCenterMappings_PrincipalObjectId",
                table: "PrincipalCostCenterMappings",
                column: "PrincipalObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_PrincipalCostCenterMappings_PrincipalType_PrincipalObjectId_CostCenterId",
                table: "PrincipalCostCenterMappings",
                columns: new[] { "PrincipalType", "PrincipalObjectId", "CostCenterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRuns_StartedUtc",
                table: "SnapshotRuns",
                column: "StartedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UsageSnapshots_CostCenterId",
                table: "UsageSnapshots",
                column: "CostCenterId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageSnapshots_Year_Month",
                table: "UsageSnapshots",
                columns: new[] { "Year", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageSnapshots_Year_Month_Day_UserLogin_Model_Sku",
                table: "UsageSnapshots",
                columns: new[] { "Year", "Month", "Day", "UserLogin", "Model", "Sku" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminPrincipals");

            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "BudgetSnapshots");

            migrationBuilder.DropTable(
                name: "PrincipalCostCenterMappings");

            migrationBuilder.DropTable(
                name: "SnapshotRuns");

            migrationBuilder.DropTable(
                name: "UsageSnapshots");
        }
    }
}
