using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Models;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for turning a GitHub budget into the fields we persist.
    ///
    /// This logic previously existed in TWO places (the snapshot job and the dev-seed path in
    /// Program.cs), both of which reduced GitHub's several budget scopes to a binary
    /// "cost_center or everything-else" decision. Everything-else was stored as the
    /// ENTERPRISE-WIDE budget under the key (Scope="Org", CostCenterId=""), so a live enterprise
    /// carrying enterprise + organization + multi_user_customer + user budgets had FOUR rows
    /// competing for ONE key — last write won, and whichever won was rendered as the
    /// enterprise-wide budget and compared against total enterprise spend.
    ///
    /// Two changes prevent that class of bug rather than just this instance of it:
    ///  1. Rows are keyed by GitHub's own stable budget id (see <see cref="KeyFor"/>), not by a
    ///     composite we invent. Collisions become impossible by construction.
    ///  2. Unknown scopes map to <see cref="BudgetScopes.Unknown"/> — NOT to the enterprise
    ///     budget. GitHub adds scopes over time (multi_user_customer was undocumented in our
    ///     model until a live probe found it); an unrecognized one must never masquerade as the
    ///     enterprise-wide budget.
    /// </summary>
    public static class BudgetScopeMapper
    {
        /// <summary>
        /// Maps GitHub's <c>budget_scope</c> string to our stored scope constant.
        /// Case-insensitive; null/blank and unrecognized values both yield
        /// <see cref="BudgetScopes.Unknown"/> so they are stored faithfully but never displayed
        /// as something they are not.
        /// </summary>
        public static string MapScope(string? gitHubScope) =>
            (gitHubScope ?? "").Trim().ToLowerInvariant() switch
            {
                "cost_center"         => BudgetScopes.CostCenter,
                "enterprise"          => BudgetScopes.Org,   // "Org" has always meant enterprise-wide here
                "organization"        => BudgetScopes.Organization,
                "user"                => BudgetScopes.User,
                "multi_user_customer" => BudgetScopes.MultiUserCustomer,
                _                     => BudgetScopes.Unknown,
            };

        /// <summary>
        /// The per-enterprise unique key for a budget row.
        ///
        /// Prefers GitHub's stable <c>id</c>. Falls back to a deterministic synthetic key when the
        /// id is missing — the mock billing client historically emitted budgets without ids, and a
        /// null-keyed fallback would silently recreate the very collision this class exists to fix.
        /// The synthetic key includes scope, entity and user so that two budgets differing only in
        /// scope still occupy different rows.
        /// </summary>
        public static string KeyFor(Budget b)
        {
            if (!string.IsNullOrWhiteSpace(b.Id)) return b.Id.Trim();
            var scope = (b.BudgetScope ?? "none").Trim().ToLowerInvariant();
            var entity = (b.BudgetEntityName ?? "").Trim().ToLowerInvariant();
            var user = (b.User ?? "").Trim().ToLowerInvariant();
            return $"synthetic:{scope}:{entity}:{user}";
        }

        /// <summary>
        /// Resolves a cost-center budget's <c>budget_entity_name</c> (a display NAME in the real
        /// API) to the stable cost-center id everything downstream keys off. Exact id match first
        /// (defensive), then case-insensitive name match; an entity naming a cost center this
        /// enterprise does not have falls back to the raw value.
        /// Returns (id, name) — both empty/null for non-cost-center scopes.
        /// </summary>
        public static (string CostCenterId, string? CostCenterName) ResolveCostCenter(
            Budget b, IReadOnlyList<CostCenter> costCenters)
        {
            if (MapScope(b.BudgetScope) != BudgetScopes.CostCenter) return ("", null);

            var entity = b.BudgetEntityName ?? "";
            var match = costCenters.FirstOrDefault(c => string.Equals(c.Id, entity, StringComparison.OrdinalIgnoreCase))
                     ?? costCenters.FirstOrDefault(c => string.Equals(c.Name, entity, StringComparison.OrdinalIgnoreCase));
            return (match?.Id ?? entity, match?.Name);
        }

        /// <summary>
        /// Projects a GitHub budget onto a persistable <see cref="BudgetSnapshot"/>'s mutable
        /// fields. Used by both the snapshot job and the dev seed so the two can never drift.
        /// </summary>
        public static void Apply(
            BudgetSnapshot row, Budget b, IReadOnlyList<CostCenter> costCenters, DateTime nowUtc)
        {
            var (ccId, ccName) = ResolveCostCenter(b, costCenters);
            row.GitHubBudgetId = KeyFor(b);
            row.Scope = MapScope(b.BudgetScope);
            row.CostCenterId = ccId;
            // Keep a previously-resolved name if this run could not resolve one.
            row.CostCenterName = ccName ?? row.CostCenterName;
            row.EntityName = b.BudgetEntityName;
            row.UserLogin = b.User;
            row.Amount = b.BudgetAmount;
            row.ConsumedAmount = b.ConsumedAmount ?? 0m;
            row.PreventFurtherUsage = b.PreventFurtherUsage ?? false;
            row.SnapshotUtc = nowUtc;
        }
    }
}
