using GhcpCreditVisibility.Models;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Synthetic data source so the entire stack (Entra auth, persistence, scoping,
    /// dashboard, snapshot job) can be deployed and demoed WITHOUT a GitHub Copilot
    /// subscription or an enterprise PAT. Data is deterministic (seeded by login) so
    /// trends are stable across runs.
    ///
    /// Multi-enterprise: the mock serves distinct data PER ENTERPRISE SLUG, deliberately
    /// exercising the collision cases the multi-enterprise schema exists for:
    ///  - "contoso"  — 12 users across Engineering / Product / Finance.
    ///  - "fabrikam" — 8 users, TWO of whom share logins with contoso users (the same GitHub
    ///    login legitimately existing in two enterprises), and a cost center ALSO named
    ///    "Engineering" (name collision; ids differ).
    ///  - "demo-broken" — always throws. The fire-drill enterprise: register it to rehearse
    ///    per-enterprise failure isolation, alerts, and SRE-agent triage with zero risk.
    ///  - any other slug — the classic cost-center-a/b/c seed, so pre-multi-enterprise
    ///    single-enterprise demos look unchanged.
    /// </summary>
    public sealed class MockGitHubBillingClient : IGitHubBillingClient
    {
        /// <summary>Registering an enterprise with this slug (mock) simulates a hard outage.</summary>
        public const string BrokenEnterpriseSlug = "demo-broken";

        // BudgetsUseEntityNames: the REAL GitHub budgets API sets budget_entity_name to the cost
        // center's display NAME, not its id — fabrikam mirrors that shape so the snapshot's
        // name→id resolution stays exercised; contoso keeps the id-shaped variant for coverage.
        private sealed record EnterpriseSeed(
            (string Login, string Name, string CostCenterId)[] Users,
            (string Id, string Name)[] CostCenters,
            decimal OrgBudget,
            IReadOnlyDictionary<string, decimal> CostCenterBudgets,
            bool BudgetsUseEntityNames = false);

        // 20 users: combined with fabrikam's 8 the demo exceeds one dashboard page (25/page), so
        // the per-user table's pagination is exercised locally, not just in real deployments.
        private static readonly EnterpriseSeed ContosoSeed = new(
            Users: new[]
            {
                ("ahernandez", "Ana Hernandez",   "cc-contoso-eng"),
                ("bwright",    "Ben Wright",      "cc-contoso-eng"),
                ("cpatel",     "Chandni Patel",   "cc-contoso-eng"),
                ("dkim",       "Daniel Kim",      "cc-contoso-eng"),
                ("squinn",     "Sam Quinn",       "cc-contoso-eng"),
                ("tberg",      "Tova Berg",       "cc-contoso-eng"),
                ("uadeyemi",   "Uche Adeyemi",    "cc-contoso-eng"),
                ("efischer",   "Erik Fischer",    "cc-contoso-product"),
                ("fgomez",     "Fernanda Gomez",  "cc-contoso-product"),
                ("gsingh",     "Gita Singh",      "cc-contoso-product"),
                ("hmuller",    "Hans Muller",     "cc-contoso-product"),
                ("vpetrov",    "Vera Petrov",     "cc-contoso-product"),
                ("wlarsen",    "Wim Larsen",      "cc-contoso-product"),
                ("xhuang",     "Xin Huang",       "cc-contoso-product"),
                ("iolsen",     "Ida Olsen",       "cc-contoso-finance"),
                ("jchen",      "Jun Chen",        "cc-contoso-finance"),
                ("krossi",     "Katya Rossi",     "cc-contoso-finance"),
                ("lnguyen",    "Linh Nguyen",     "cc-contoso-finance"),
                ("ymendez",    "Yara Mendez",     "cc-contoso-finance"),
                ("zokafor",    "Zara Okafor",     "cc-contoso-finance"),
            },
            CostCenters: new[]
            {
                ("cc-contoso-eng",     "Engineering"),
                ("cc-contoso-product", "Product"),
                ("cc-contoso-finance", "Finance"),
            },
            // Budgets sized for 20 users (~$40/user/month of synthetic spend) so the demo shows a
            // realistic status mix rather than everything blowing over.
            OrgBudget: 1050m,
            CostCenterBudgets: new Dictionary<string, decimal>
            {
                ["cc-contoso-eng"] = 320m, ["cc-contoso-product"] = 380m, ["cc-contoso-finance"] = 330m
            });

        // fabrikam deliberately overlaps: "dkim" and "jchen" exist in BOTH enterprises (distinct
        // usage rows per enterprise — that's how GitHub bills), and its "Engineering" cost center
        // collides by NAME with contoso's (ids differ — names alone are ambiguous by design).
        private static readonly EnterpriseSeed FabrikamSeed = new(
            Users: new[]
            {
                ("dkim",       "Daniel Kim",      "cc-fabrikam-eng"),
                ("jchen",      "Jun Chen",        "cc-fabrikam-eng"),
                ("mtanaka",    "Mia Tanaka",      "cc-fabrikam-eng"),
                ("noduya",     "Nate Oduya",      "cc-fabrikam-eng"),
                ("opark",      "Olivia Park",     "cc-fabrikam-research"),
                ("pkowalski",  "Piotr Kowalski",  "cc-fabrikam-research"),
                ("qzhao",      "Qiang Zhao",      "cc-fabrikam-research"),
                ("rsilva",     "Rafaela Silva",   "cc-fabrikam-research"),
            },
            CostCenters: new[]
            {
                ("cc-fabrikam-eng",      "Engineering"),
                ("cc-fabrikam-research", "Research"),
            },
            OrgBudget: 450m,
            CostCenterBudgets: new Dictionary<string, decimal>
            {
                ["cc-fabrikam-eng"] = 220m, ["cc-fabrikam-research"] = 200m
            },
            BudgetsUseEntityNames: true);

        // Classic seed — served for any unrecognized slug so existing single-enterprise
        // deployments/demos (and tests) see exactly the data they always did.
        private static readonly EnterpriseSeed LegacySeed = new(
            Users: new[]
            {
                ("ahernandez", "Ana Hernandez",   "cost-center-a"),
                ("bwright",    "Ben Wright",      "cost-center-a"),
                ("cpatel",     "Chandni Patel",   "cost-center-a"),
                ("dkim",       "Daniel Kim",      "cost-center-a"),
                ("efischer",   "Erik Fischer",    "cost-center-b"),
                ("fgomez",     "Fernanda Gomez",  "cost-center-b"),
                ("gsingh",     "Gita Singh",      "cost-center-b"),
                ("hmuller",    "Hans Muller",     "cost-center-b"),
                ("iolsen",     "Ida Olsen",       "cost-center-c"),
                ("jchen",      "Jun Chen",        "cost-center-c"),
                ("krossi",     "Katya Rossi",     "cost-center-c"),
                ("lnguyen",    "Linh Nguyen",     "cost-center-c"),
            },
            CostCenters: new[]
            {
                ("cost-center-a", "Cost Center A"),
                ("cost-center-b", "Cost Center B"),
                ("cost-center-c", "Cost Center C"),
            },
            OrgBudget: 700m,
            CostCenterBudgets: new Dictionary<string, decimal>
            {
                ["cost-center-a"] = 180m, ["cost-center-b"] = 250m, ["cost-center-c"] = 300m
            });

        private static readonly (string Model, decimal Price)[] Models =
        {
            ("gpt-5",              0.04m),
            ("claude-sonnet-4.5",  0.04m),
            ("o4-mini",            0.01m),
        };

        private static EnterpriseSeed SeedFor(string enterprise) => enterprise?.ToLowerInvariant() switch
        {
            "contoso" => ContosoSeed,
            "fabrikam" => FabrikamSeed,
            _ => LegacySeed
        };

        private static void ThrowIfBroken(string enterprise)
        {
            if (string.Equals(enterprise, BrokenEnterpriseSlug, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Simulated outage: mock enterprise '{BrokenEnterpriseSlug}' always fails. " +
                    "This is the fire-drill enterprise — use it to rehearse per-enterprise failure isolation and alerting.");
        }

        public Task<IReadOnlyList<EnterpriseLicenseUser>> GetEnterpriseUsersAsync(string enterprise, CancellationToken ct = default)
        {
            ThrowIfBroken(enterprise);
            IReadOnlyList<EnterpriseLicenseUser> users = SeedFor(enterprise).Users
                .Select(s => new EnterpriseLicenseUser { GitHubComLogin = s.Login, GitHubComName = s.Name })
                .ToList();
            return Task.FromResult(users);
        }

        public Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string enterprise, CancellationToken ct = default)
        {
            ThrowIfBroken(enterprise);
            var seed = SeedFor(enterprise);
            IReadOnlyList<CostCenter> ccs = seed.CostCenters.Select(c => new CostCenter
            {
                Id = c.Id,
                Name = c.Name,
                Resources = seed.Users.Where(s => s.CostCenterId == c.Id)
                                .Select(s => new CostCenterResource { Type = "User", Name = s.Login })
                                .ToList()
            }).ToList();
            return Task.FromResult(ccs);
        }

        public Task<IReadOnlyList<Budget>> GetBudgetsAsync(string enterprise, CancellationToken ct = default)
        {
            ThrowIfBroken(enterprise);
            var seed = SeedFor(enterprise);
            // GitHub-governed budgets (this app only reads them). Amounts are illustrative monthly
            // totals: an org/enterprise-wide budget plus a per-cost-center budget.
            var budgets = new List<Budget>
            {
                new() { BudgetProductSku = "ai_credits", BudgetScope = "enterprise", BudgetAmount = seed.OrgBudget },
            };
            budgets.AddRange(seed.CostCenters.Select(c => new Budget
            {
                BudgetProductSku = "ai_credits",
                BudgetScope = "cost_center",
                BudgetEntityName = seed.BudgetsUseEntityNames ? c.Name : c.Id,
                BudgetAmount = seed.CostCenterBudgets.GetValueOrDefault(c.Id, 250m),
                ConsumedAmount = ConsumedForCostCenter(seed, enterprise, c.Id)
            }));
            IReadOnlyList<Budget> result = budgets;
            return Task.FromResult(result);
        }

        public Task<UserCreditUsage?> GetCurrentMonthUsageForUserAsync(string enterprise, string user, CancellationToken ct = default)
        {
            ThrowIfBroken(enterprise);
            var seed = SeedFor(enterprise);
            var s = seed.Users.FirstOrDefault(s => string.Equals(s.Login, user, StringComparison.OrdinalIgnoreCase));
            if (s.Login is null) return Task.FromResult<UserCreditUsage?>(null);

            var now = DateTime.UtcNow;
            // Seed the RNG with (enterprise, user, month) so a login that exists in two enterprises
            // gets DIFFERENT spend in each — visibly distinct rows, as with real billing.
            var rng = new Random(StableSeed(enterprise + "|" + user) + now.Year * 100 + now.Month);
            var items = new List<UsageItem>();
            foreach (var (model, price) in Models)
            {
                var qty = rng.Next(50, 900);
                var net = Math.Round(qty * price, 2);
                items.Add(new UsageItem
                {
                    Product = "copilot",
                    Sku = "ai_credits",
                    Model = model,
                    UnitType = "credit",
                    PricePerUnit = price,
                    GrossQuantity = qty,
                    GrossAmount = net,
                    DiscountQuantity = 0,
                    DiscountAmount = 0,
                    NetQuantity = qty,
                    NetAmount = net
                });
            }

            var cc = seed.CostCenters.First(c => c.Id == s.CostCenterId);
            var usage = new UserCreditUsage
            {
                Enterprise = enterprise,
                User = s.Login,
                Product = "copilot",
                TimePeriod = new TimePeriod { Year = now.Year, Month = now.Month },
                CostCenter = new CostCenter { Id = cc.Id, Name = cc.Name },
                UsageItems = items
            };
            return Task.FromResult<UserCreditUsage?>(usage);
        }

        private static decimal ConsumedForCostCenter(EnterpriseSeed seed, string enterprise, string ccId)
        {
            var now = DateTime.UtcNow;
            decimal total = 0m;
            foreach (var s in seed.Users.Where(s => s.CostCenterId == ccId))
            {
                var rng = new Random(StableSeed(enterprise + "|" + s.Login) + now.Year * 100 + now.Month);
                foreach (var (_, price) in Models) total += Math.Round(rng.Next(50, 900) * price, 2);
            }
            return total;
        }

        /// <summary>
        /// LOCAL-DEV ONLY: synthesize DAILY history rows for every mock user of ONE enterprise across
        /// the last <paramref name="months"/> months, so the Reports page can bucket by day, week or
        /// month. Deterministic per (enterprise, user, day). Real GitHub billing exposes month-level
        /// aggregates (Day = 1); this daily fabrication is purely to make the local preview's
        /// granularity toggle meaningful. Not part of IGitHubBillingClient.
        /// </summary>
        public static IReadOnlyList<Data.UsageSnapshot> BuildHistorySnapshots(
            int months, DateTime asOfUtc, long enterpriseId = Data.Enterprise.DefaultId, string enterpriseSlug = "")
        {
            var seed = SeedFor(enterpriseSlug);
            var rows = new List<Data.UsageSnapshot>();
            var start = asOfUtc.Date.AddMonths(-Math.Max(1, months)).AddDays(1);
            var end = asOfUtc.Date;
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                // Mild weekday seasonality: lighter usage on weekends.
                bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                foreach (var s in seed.Users)
                {
                    // "mtanaka" (fabrikam) is a deliberate recent joiner: no fabricated history, so
                    // her first snapshot month renders the "new" badge in the per-user delta column.
                    if (string.Equals(s.Login, "mtanaka", StringComparison.OrdinalIgnoreCase)) continue;
                    var cc = seed.CostCenters.First(c => c.Id == s.CostCenterId);
                    var rng = new Random(StableSeed(enterpriseSlug + "|" + s.Login) + day.Year * 10000 + day.Month * 100 + day.Day);
                    foreach (var (model, price) in Models)
                    {
                        // Daily quantities are tuned so a full fabricated month lands in the same
                        // ballpark as the snapshot job's current-month aggregates (avg ~$40/user) —
                        // month-over-month deltas then spread realistically around zero instead of
                        // uniformly spiking on the seam between fabricated and snapshotted months.
                        var qty = rng.Next(0, weekend ? 10 : 38); // credits used that day for this model
                        if (qty == 0) continue;                   // some days a user doesn't touch a model
                        var net = Math.Round(qty * price, 2);
                        rows.Add(new Data.UsageSnapshot
                        {
                            EnterpriseId = enterpriseId,
                            SnapshotUtc = asOfUtc,
                            Year = day.Year,
                            Month = day.Month,
                            Day = day.Day,
                            UserLogin = s.Login,
                            UserName = s.Name,
                            CostCenterId = cc.Id,
                            CostCenterName = cc.Name,
                            Product = "copilot",
                            Sku = "ai_credits",
                            Model = model,
                            NetQuantity = qty,
                            NetAmount = net,
                            GrossAmount = net
                        });
                    }
                }
            }
            return rows;
        }

        private static int StableSeed(string s)
        {
            unchecked
            {
                int hash = 17;
                foreach (var ch in s) hash = hash * 31 + ch;
                return Math.Abs(hash);
            }
        }
    }
}
