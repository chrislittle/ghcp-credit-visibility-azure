using System.Linq.Expressions;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>What a line on GitHub's enterprise billing feed is, as far as Copilot is concerned.</summary>
    public enum CopilotLineKind { NotCopilot, AiCredits, License, OtherCopilot }

    /// <summary>
    /// Classifies <see cref="OrgUsageSnapshot"/> rows. That table holds EVERY product GitHub bills
    /// the enterprise for — Actions, storage, GHAS, GHEC seats — not just Copilot, so anything that
    /// sums it for a Copilot figure must filter first. Summing it raw is how an organization's
    /// "Copilot" budget quietly picked up its Actions minutes.
    ///
    /// Values seen on the live API: AI credits arrive as product <c>Copilot</c>, sku
    /// <c>Copilot AI Credits</c>, unit <c>ai-credits</c>; seats as sku <c>Copilot Business</c> in
    /// prorated user-months (15/31 of a seat for half a month). Matching is case-insensitive in memory
    /// because GitHub has used both <c>Copilot</c> and <c>copilot</c> across endpoints.
    /// </summary>
    public static class CopilotBillingLines
    {
        public const string AiCreditsSku = "Copilot AI Credits";

        /// <summary>SQL-translatable AI-credit test, for queries that aggregate in the database.
        /// Unit type OR sku, so a row is still recognised if GitHub ever drops one of the two.</summary>
        public static readonly Expression<Func<OrgUsageSnapshot, bool>> IsAiCreditExpr =
            o => o.UnitType == UsageUnitTypes.AiCredits || o.Sku == AiCreditsSku;

        public static CopilotLineKind Classify(string? product, string? sku, string? unitType)
        {
            if (Eq(unitType, UsageUnitTypes.AiCredits) || Eq(sku, AiCreditsSku)) return CopilotLineKind.AiCredits;

            var isCopilot = Eq(product, "copilot") ||
                            (sku?.StartsWith("copilot", StringComparison.OrdinalIgnoreCase) ?? false);
            if (!isCopilot) return CopilotLineKind.NotCopilot;

            // Seat charges: "Copilot Business" / "Copilot Enterprise", billed in (prorated) user-months.
            var isSeatSku = sku is not null &&
                            (sku.EndsWith("Business", StringComparison.OrdinalIgnoreCase) ||
                             sku.EndsWith("Enterprise", StringComparison.OrdinalIgnoreCase));
            var isMonthUnit = unitType?.Contains("month", StringComparison.OrdinalIgnoreCase) ?? false;
            return isSeatSku || isMonthUnit ? CopilotLineKind.License : CopilotLineKind.OtherCopilot;
        }

        public static CopilotLineKind Classify(OrgUsageSnapshot o) => Classify(o.Product, o.Sku, o.UnitType);

        private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
