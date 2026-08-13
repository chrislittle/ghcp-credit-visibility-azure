using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class EnterpriseCopilotSeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnterpriseCopilotSeats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EnterpriseId = table.Column<long>(type: "bigint", nullable: false),
                    PlanType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Seats = table.Column<int>(type: "int", nullable: false),
                    SnapshotUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnterpriseCopilotSeats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnterpriseCopilotSeats_EnterpriseId_PlanType",
                table: "EnterpriseCopilotSeats",
                columns: new[] { "EnterpriseId", "PlanType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnterpriseCopilotSeats");
        }
    }
}
