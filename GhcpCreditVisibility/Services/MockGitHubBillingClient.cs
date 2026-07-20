using GhcpCreditVisibility.Models;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Synthetic data source so the entire stack (Entra auth, persistence, scoping,
    /// dashboard, snapshot job) can be deployed and demoed WITHOUT a GitHub Copilot
    /// subscription or an enterprise PAT. Enabled with GitHub:UseMock = true.
    /// Data is deterministic (seeded by login) so trends are stable across runs.
    /// </summary>
    public sealed class MockGitHubBillingClient : IGitHubBillingClient
    {
        private static readonly (string Login, string Name, string CostCenterId)[] Seed =
        {
            ("ahernandez", "Ana Hernandez",   "cost-center-a"),
            ("bwright",    "Ben Wright",       "cost-center-a"),
            ("cpatel",     "Chandni Patel",    "cost-center-a"),
            ("dkim",       "Daniel Kim",       "cost-center-a"),
            ("efischer",   "Erik Fischer",     "cost-center-b"),
            ("fgomez",     "Fernanda Gomez",   "cost-center-b"),
            ("gsingh",     "Gita Singh",       "cost-center-b"),
            ("hmuller",    "Hans Muller",      "cost-center-b"),
            ("iolsen",     "Ida Olsen",        "cost-center-c"),
            ("jchen",      "Jun Chen",         "cost-center-c"),
            ("krossi",     "Katya Rossi",      "cost-center-c"),
            ("lnguyen",    "Linh Nguyen",      "cost-center-c"),
        };

        private static readonly (string Id, string Name)[] CostCenterDefs =
        {
            ("cost-center-a", "Cost Center A"),
            ("cost-center-b", "Cost Center B"),
            ("cost-center-c", "Cost Center C"),
        };

        private static readonly (string Model, decimal Price)[] Models =
        {
            ("gpt-5",              0.04m),
            ("claude-sonnet-4.5",  0.04m),
            ("o4-mini",            0.01m),
        };

        public Task<IReadOnlyList<EnterpriseLicenseUser>> GetEnterpriseUsersAsync(string enterprise, CancellationToken ct = default)
        {
            IReadOnlyList<EnterpriseLicenseUser> users = Seed
                .Select(s => new EnterpriseLicenseUser { GitHubComLogin = s.Login, GitHubComName = s.Name })
                .ToList();
            return Task.FromResult(users);
        }

        public Task<IReadOnlyList<CostCenter>> GetCostCentersAsync(string enterprise, CancellationToken ct = default)
        {
            IReadOnlyList<CostCenter> ccs = CostCenterDefs.Select(c => new CostCenter
            {
                Id = c.Id,
                Name = c.Name,
                Resources = Seed.Where(s => s.CostCenterId == c.Id)
                                .Select(s => new CostCenterResource { Type = "User", Name = s.Login })
                                .ToList()
            }).ToList();
            return Task.FromResult(ccs);
        }

        public Task<IReadOnlyList<Budget>> GetBudgetsAsync(string enterprise, CancellationToken ct = default)
        {
            // GitHub-governed budgets (this app only reads them). Amounts are illustrative monthly
            // totals: an org/enterprise-wide budget plus a per-cost-center budget.
            var ccBudget = new Dictionary<string, decimal> { ["cost-center-a"] = 180m, ["cost-center-b"] = 250m, ["cost-center-c"] = 300m };
            var budgets = new List<Budget>
            {
                new() { BudgetProductSku = "ai_credits", BudgetScope = "enterprise", BudgetAmount = 700m },
            };
            budgets.AddRange(CostCenterDefs.Select(c => new Budget
            {
                BudgetProductSku = "ai_credits",
                BudgetScope = "cost_center",
                BudgetEntityName = c.Id,
                BudgetAmount = ccBudget.GetValueOrDefault(c.Id, 250m),
                ConsumedAmount = ConsumedForCostCenter(c.Id)
            }));
            IReadOnlyList<Budget> result = budgets;
            return Task.FromResult(result);
        }

        public Task<UserCreditUsage?> GetCurrentMonthUsageForUserAsync(string enterprise, string user, CancellationToken ct = default)
        {
            var seed = Seed.FirstOrDefault(s => string.Equals(s.Login, user, StringComparison.OrdinalIgnoreCase));
            if (seed.Login is null) return Task.FromResult<UserCreditUsage?>(null);

            var now = DateTime.UtcNow;
            var rng = new Random(StableSeed(user) + now.Year * 100 + now.Month); // deterministic per user/month
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

            var cc = CostCenterDefs.First(c => c.Id == seed.CostCenterId);
            var usage = new UserCreditUsage
            {
                Enterprise = enterprise,
                User = seed.Login,
                Product = "copilot",
                TimePeriod = new TimePeriod { Year = now.Year, Month = now.Month },
                CostCenter = new CostCenter { Id = cc.Id, Name = cc.Name },
                UsageItems = items
            };
            return Task.FromResult<UserCreditUsage?>(usage);
        }

        private static decimal ConsumedForCostCenter(string ccId)
        {
            var now = DateTime.UtcNow;
            decimal total = 0m;
            foreach (var s in Seed.Where(s => s.CostCenterId == ccId))
            {
                var rng = new Random(StableSeed(s.Login) + now.Year * 100 + now.Month);
                foreach (var (_, price) in Models) total += Math.Round(rng.Next(50, 900) * price, 2);
            }
            return total;
        }

        /// <summary>
        /// LOCAL-DEV ONLY: synthesize DAILY history rows for every mock user across the last
        /// <paramref name="months"/> months, so the Reports page can bucket by day, week or month.
        /// Deterministic per user/day. Real GitHub billing exposes month-level aggregates (Day = 1);
        /// this daily fabrication is purely to make the local preview's granularity toggle meaningful.
        /// Not part of IGitHubBillingClient.
        /// </summary>
        public static IReadOnlyList<Data.UsageSnapshot> BuildHistorySnapshots(int months, DateTime asOfUtc)
        {
            var rows = new List<Data.UsageSnapshot>();
            var start = asOfUtc.Date.AddMonths(-Math.Max(1, months)).AddDays(1);
            var end = asOfUtc.Date;
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                // Mild weekday seasonality: lighter usage on weekends.
                bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                foreach (var s in Seed)
                {
                    var cc = CostCenterDefs.First(c => c.Id == s.CostCenterId);
                    var rng = new Random(StableSeed(s.Login) + day.Year * 10000 + day.Month * 100 + day.Day);
                    foreach (var (model, price) in Models)
                    {
                        var qty = rng.Next(0, weekend ? 6 : 22); // credits used that day for this model
                        if (qty == 0) continue;                  // some days a user doesn't touch a model
                        var net = Math.Round(qty * price, 2);
                        rows.Add(new Data.UsageSnapshot
                        {
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
