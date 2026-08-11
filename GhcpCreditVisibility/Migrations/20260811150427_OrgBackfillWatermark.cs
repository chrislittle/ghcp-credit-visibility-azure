using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class OrgBackfillWatermark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrgBackfillOldestMonth",
                table: "Enterprises",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrgBackfillOldestYear",
                table: "Enterprises",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrgBackfillOldestMonth",
                table: "Enterprises");

            migrationBuilder.DropColumn(
                name: "OrgBackfillOldestYear",
                table: "Enterprises");
        }
    }
}
