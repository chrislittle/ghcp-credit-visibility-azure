using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class DailyUsageHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyUsageSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EnterpriseId = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_DailyUsageSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyUsageSnapshots_EnterpriseId_Year_Month_Day",
                table: "DailyUsageSnapshots",
                columns: new[] { "EnterpriseId", "Year", "Month", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyUsageSnapshots_EnterpriseId_Year_Month_Day_UserLogin_Model_Sku",
                table: "DailyUsageSnapshots",
                columns: new[] { "EnterpriseId", "Year", "Month", "Day", "UserLogin", "Model", "Sku" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyUsageSnapshots");
        }
    }
}
