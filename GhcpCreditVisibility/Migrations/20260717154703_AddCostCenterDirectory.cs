using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class AddCostCenterDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostCenterDirectory",
                columns: table => new
                {
                    CostCenterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CurrentName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostCenterDirectory", x => x.CostCenterId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CostCenterDirectory");
        }
    }
}
