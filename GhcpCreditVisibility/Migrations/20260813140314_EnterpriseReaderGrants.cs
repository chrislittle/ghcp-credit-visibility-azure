using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GhcpCreditVisibility.Migrations
{
    /// <inheritdoc />
    public partial class EnterpriseReaderGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrincipalEnterpriseGrants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PrincipalType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PrincipalObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PrincipalDisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    EnterpriseId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrincipalEnterpriseGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrincipalEnterpriseGrants_PrincipalObjectId",
                table: "PrincipalEnterpriseGrants",
                column: "PrincipalObjectId");

            // NO filter clause. EF's SQL Server default for a nullable column in a unique index is
            // "WHERE [EnterpriseId] IS NOT NULL", which would exclude the all-enterprises rows this
            // constraint most needs to police — duplicate all-grants for one principal would be
            // permitted. Unfiltered, SQL Server treats NULLs as equal and rejects the second one.
            migrationBuilder.CreateIndex(
                name: "IX_PrincipalEnterpriseGrants_PrincipalType_PrincipalObjectId_EnterpriseId",
                table: "PrincipalEnterpriseGrants",
                columns: new[] { "PrincipalType", "PrincipalObjectId", "EnterpriseId" },
                unique: true);

            // ── GRANDFATHER EXISTING ADMINS ──────────────────────────────────────────────────
            // Until this migration, being an application administrator implied seeing everything.
            // That is exactly what this change undoes — but undoing it retroactively would strip
            // every existing admin of all data visibility the moment this deploys, with no symptom
            // beyond an empty dashboard. Each existing admin principal therefore receives an
            // all-enterprises reader grant, preserving precisely what they had.
            //
            // NEW admins get no such grant: from here on the two rights are granted separately.
            //
            // Idempotent by construction (NOT EXISTS), so a re-run cannot duplicate rows, and it
            // runs inside the migration rather than at startup so it happens exactly once under the
            // existing DatabaseMigratorHostedService lease.
            migrationBuilder.Sql(@"
INSERT INTO [PrincipalEnterpriseGrants]
    ([PrincipalType], [PrincipalObjectId], [PrincipalDisplayName], [EnterpriseId], [CreatedUtc], [ModifiedBy])
SELECT a.[PrincipalType], a.[PrincipalObjectId], a.[PrincipalDisplayName], NULL, SYSUTCDATETIME(),
       'migration:EnterpriseReaderGrants'
FROM [AdminPrincipals] a
WHERE NOT EXISTS (
    SELECT 1 FROM [PrincipalEnterpriseGrants] g
    WHERE g.[PrincipalType] = a.[PrincipalType]
      AND g.[PrincipalObjectId] = a.[PrincipalObjectId]
      AND g.[EnterpriseId] IS NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrincipalEnterpriseGrants");
        }
    }
}
