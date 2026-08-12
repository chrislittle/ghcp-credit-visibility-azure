using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class AddUserBackfillState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LicensedUserCount",
                table: "Enterprises",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UserBackfillEnabled",
                table: "Enterprises",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UserBackfillOldestMonth",
                table: "Enterprises",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UserBackfillOldestYear",
                table: "Enterprises",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LicensedUserCount",
                table: "Enterprises");

            migrationBuilder.DropColumn(
                name: "UserBackfillEnabled",
                table: "Enterprises");

            migrationBuilder.DropColumn(
                name: "UserBackfillOldestMonth",
                table: "Enterprises");

            migrationBuilder.DropColumn(
                name: "UserBackfillOldestYear",
                table: "Enterprises");
        }
    }
}
