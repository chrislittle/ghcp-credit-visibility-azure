using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Models;
using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Regression cover for the budget-scope collapse.
    ///
    /// A live probe of the real API returned five budgets in five distinct scopes — cost_center,
    /// enterprise, organization, multi_user_customer and user. The previous mapping reduced that to
    /// "cost_center or everything-else", storing four of the five as the ENTERPRISE-WIDE budget
    /// under the single key (Scope="Org", CostCenterId=""). They overwrote each other on every
    /// snapshot run, and whichever survived was displayed as the enterprise budget and compared
    /// against total enterprise spend.
    /// </summary>
    public class BudgetScopeMappingTests
    {
        private static readonly IReadOnlyList<CostCenter> CostCenters = new List<CostCenter>
        {
            new() { Id = "cc-eng", Name = "Engineering" },
        };

        private static Budget Gh(string id, string scope, string? entity = null, string? user = null) =>
            new() { Id = id, BudgetScope = scope, BudgetEntityName = entity, User = user, BudgetAmount = 100m };

        [Theory]
        [InlineData("cost_center", BudgetScopes.CostCenter)]
        [InlineData("enterprise", BudgetScopes.Org)]
        [InlineData("organization", BudgetScopes.Organization)]
        [InlineData("user", BudgetScopes.User)]
        [InlineData("multi_user_customer", BudgetScopes.MultiUserCustomer)]
        [InlineData("COST_CENTER", BudgetScopes.CostCenter)]   // case-insensitive
        public void MapScope_maps_every_scope_the_live_api_returns(string gitHubScope, string expected)
            => Assert.Equal(expected, BudgetScopeMapper.MapScope(gitHubScope));

        /// <summary>
        /// The important one. An unrecognized scope must NOT become the enterprise budget —
        /// multi_user_customer was undocumented in our model until a live probe surfaced it, so
        /// GitHub introducing another is a matter of when, not if.
        /// </summary>
        [Theory]
        [InlineData("some_future_scope")]
        [InlineData("")]
        [InlineData(null)]
        public void MapScope_never_lets_an_unknown_scope_impersonate_the_enterprise_budget(string? scope)
        {
            var mapped = BudgetScopeMapper.MapScope(scope);
            Assert.Equal(BudgetScopes.Unknown, mapped);
            Assert.NotEqual(BudgetScopes.Org, mapped);
        }

        /// <summary>The collapse itself: five scopes must occupy five distinct rows.</summary>
        [Fact]
        public void Five_scopes_produce_five_distinct_keys()
        {
            var budgets = new[]
            {
                Gh("b1", "enterprise"),
                Gh("b2", "cost_center", entity: "Engineering"),
                Gh("b3", "organization", entity: "acme-org"),
                Gh("b4", "multi_user_customer", entity: "acme-muc"),
                Gh("b5", "user", entity: "octocat", user: "octocat"),
            };

            var keys = budgets.Select(BudgetScopeMapper.KeyFor).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(5, keys.Count);

            var rows = budgets.Select(b =>
            {
                var r = new BudgetSnapshot { EnterpriseId = 1 };
                BudgetScopeMapper.Apply(r, b, CostCenters, DateTime.UtcNow);
                return r;
            }).ToList();

            // Under the old (Scope, CostCenterId) key these four all became ("Org", "").
            Assert.Equal(5, rows.Select(r => r.GitHubBudgetId).Distinct().Count());
            Assert.Single(rows, r => r.Scope == BudgetScopes.Org);
            Assert.Single(rows, r => r.Scope == BudgetScopes.User);
            Assert.Single(rows, r => r.Scope == BudgetScopes.Organization);
            Assert.Single(rows, r => r.Scope == BudgetScopes.MultiUserCustomer);
        }

        /// <summary>Budgets with no id (the mock client emitted these) must still not collide.</summary>
        [Fact]
        public void Id_less_budgets_fall_back_to_distinct_synthetic_keys()
        {
            var a = new Budget { BudgetScope = "enterprise", BudgetAmount = 1m };
            var b = new Budget { BudgetScope = "user", BudgetEntityName = "octocat", User = "octocat", BudgetAmount = 2m };
            var c = new Budget { BudgetScope = "organization", BudgetEntityName = "acme", BudgetAmount = 3m };

            var keys = new[] { a, b, c }.Select(BudgetScopeMapper.KeyFor).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(3, keys.Count);
        }

        [Fact]
        public void KeyFor_prefers_the_github_id_and_is_stable_across_runs()
        {
            var b = Gh("budget-abc", "user", user: "octocat");
            Assert.Equal("budget-abc", BudgetScopeMapper.KeyFor(b));
            Assert.Equal(BudgetScopeMapper.KeyFor(b), BudgetScopeMapper.KeyFor(b));
        }

        /// <summary>Cost-center budgets name the cost center by DISPLAY NAME; everything downstream keys off the id.</summary>
        [Fact]
        public void Cost_center_budgets_resolve_entity_name_to_the_stable_id()
        {
            var (id, name) = BudgetScopeMapper.ResolveCostCenter(Gh("b", "cost_center", entity: "Engineering"), CostCenters);
            Assert.Equal("cc-eng", id);
            Assert.Equal("Engineering", name);
        }

        [Fact]
        public void Non_cost_center_scopes_carry_no_cost_center()
        {
            foreach (var scope in new[] { "enterprise", "user", "organization", "multi_user_customer" })
            {
                var (id, name) = BudgetScopeMapper.ResolveCostCenter(Gh("b", scope, entity: "something"), CostCenters);
                Assert.Equal("", id);
                Assert.Null(name);
            }
        }

        /// <summary>prevent_further_usage distinguishes an alert from a developer-blocking hard stop.</summary>
        [Fact]
        public void PreventFurtherUsage_round_trips_and_defaults_to_alert_only()
        {
            var hard = new Budget { Id = "b1", BudgetScope = "user", PreventFurtherUsage = true, BudgetAmount = 40m };
            var soft = new Budget { Id = "b2", BudgetScope = "user", PreventFurtherUsage = null, BudgetAmount = 40m };

            var hardRow = new BudgetSnapshot(); BudgetScopeMapper.Apply(hardRow, hard, CostCenters, DateTime.UtcNow);
            var softRow = new BudgetSnapshot(); BudgetScopeMapper.Apply(softRow, soft, CostCenters, DateTime.UtcNow);

            Assert.True(hardRow.PreventFurtherUsage);
            // Absent means "not stated" — never guess a hard stop.
            Assert.False(softRow.PreventFurtherUsage);
        }

        [Fact]
        public void User_login_is_captured_for_user_scoped_budgets()
        {
            var row = new BudgetSnapshot();
            BudgetScopeMapper.Apply(row, Gh("b", "user", entity: "octocat", user: "octocat"), CostCenters, DateTime.UtcNow);
            Assert.Equal("octocat", row.UserLogin);
            Assert.Equal(BudgetScopes.User, row.Scope);
        }

        /// <summary>
        /// The display allowlist is the guarantee that storing a scope never surfaces it.
        ///
        /// ORGANIZATION was added once OrgUsageSnapshots made per-organization actuals computable —
        /// a deliberate widening, which is exactly what the allowlist is for. USER stays out because
        /// the access policy for personal spending limits is undecided (not because it is
        /// incomputable), and MULTI_USER_CUSTOMER because its semantics are unestablished.
        /// </summary>
        [Fact]
        public void Only_scopes_with_computable_actuals_are_displayable()
        {
            Assert.Contains(BudgetScopes.Org, BudgetScopes.Displayable);
            Assert.Contains(BudgetScopes.CostCenter, BudgetScopes.Displayable);
            Assert.Contains(BudgetScopes.Organization, BudgetScopes.Displayable);

            Assert.DoesNotContain(BudgetScopes.User, BudgetScopes.Displayable);
            Assert.DoesNotContain(BudgetScopes.MultiUserCustomer, BudgetScopes.Displayable);
            Assert.DoesNotContain(BudgetScopes.Unknown, BudgetScopes.Displayable);
        }

        /// <summary>
        /// A displayed scope that cannot be narrowed by the viewer's access scope MUST require
        /// enterprise-grain read. Organization actuals come from a table with no cost center column,
        /// so there is nothing to filter on — displaying it to a manager would expose organizations
        /// they have no grant for.
        /// </summary>
        [Fact]
        public void Scopes_that_cannot_be_scope_filtered_need_enterprise_grain_read()
        {
            Assert.Contains(BudgetScopes.Organization, BudgetScopes.EnterpriseGrainOnly);

            // Everything gated must actually be displayable, or the flag is meaningless.
            Assert.All(BudgetScopes.EnterpriseGrainOnly, s => Assert.Contains(s, BudgetScopes.Displayable));

            // Cost center budgets ARE scope-filterable, so they must not be gated.
            Assert.DoesNotContain(BudgetScopes.CostCenter, BudgetScopes.EnterpriseGrainOnly);
        }

        /// <summary>"MultiUserCustomer" is 17 chars — the Scope column was nvarchar(16).</summary>
        [Fact]
        public void Every_scope_constant_fits_the_column()
        {
            foreach (var s in new[]
            {
                BudgetScopes.Org, BudgetScopes.CostCenter, BudgetScopes.User,
                BudgetScopes.Organization, BudgetScopes.MultiUserCustomer, BudgetScopes.Unknown,
            })
            {
                Assert.True(s.Length <= 32, $"Scope '{s}' ({s.Length} chars) exceeds the column width.");
            }
        }
    }
}
