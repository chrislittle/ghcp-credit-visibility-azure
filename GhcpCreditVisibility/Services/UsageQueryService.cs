using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>Scope-aware read model for the dashboard. Reads ONLY from the database
    /// (populated by the snapshot job) and filters every query to the caller's
    /// <see cref="UserScope"/> so a manager sees only their people.</summary>
    public sealed class UsageQueryService
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        public UsageQueryService(IDbContextFactory<BillingDbContext> dbFactory) => _dbFactory = dbFactory;

        // GrossAmount is carried alongside NetAmount so the dashboard can optionally show real
        // usage activity even when it's fully covered by an included allowance (NetAmount = 0) —
        // see the "Dashboard:ShowGrossUsage" feature flag.
        public sealed record UserMonthTotal(string UserLogin, string? UserName, string? CostCenterId, string? CostCenterName, decimal NetAmount, decimal GrossAmount = 0m);
        public sealed record CostCenterTotal(string? CostCenterId, string? CostCenterName, decimal NetAmount, decimal GrossAmount = 0m);
        public sealed record ModelTotal(string Model, decimal NetAmount, decimal GrossAmount = 0m);
        public sealed record TrendPoint(int Year, int Month, decimal NetAmount);
        public sealed record MonthOption(int Year, int Month);

        /// <summary>Distinct (year, month) periods present in the caller's scope, newest first — drives the month selector.</summary>
        public async Task<IReadOnlyList<MonthOption>> GetAvailableMonthsAsync(UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await ApplyScope(db.UsageSnapshots, scope).Select(x => new { x.Year, x.Month }).ToListAsync(ct);
            return rows.GroupBy(x => new { x.Year, x.Month })
                .Select(g => new MonthOption(g.Key.Year, g.Key.Month))
                .OrderByDescending(m => m.Year).ThenByDescending(m => m.Month)
                .ToList();
        }

        private static IQueryable<UsageSnapshot> ApplyScope(IQueryable<UsageSnapshot> q, UserScope scope)
        {
            if (scope.SeesAll) return q;
            if (scope.CostCenterIds.Count > 0) q = q.Where(x => x.CostCenterId != null && scope.CostCenterIds.Contains(x.CostCenterId));
            if (scope.UserLogins.Count > 0) q = q.Where(x => scope.UserLogins.Contains(x.UserLogin));
            if (scope.CostCenterIds.Count == 0 && scope.UserLogins.Count == 0) q = q.Where(_ => false); // no access
            return q;
        }

        /// <summary>Loads the cost-center directory (id -> CURRENT name, refreshed every snapshot run)
        /// so display names stay current even for historical rows that froze an old name at write time.</summary>
        private static async Task<Dictionary<string, string?>> LoadCurrentNamesAsync(BillingDbContext db, CancellationToken ct)
            => await db.CostCenterDirectory.ToDictionaryAsync(x => x.CostCenterId, x => x.CurrentName, ct);

        /// <summary>Prefers the directory's current name over the frozen per-row name; falls back to the
        /// frozen name if the id isn't in the directory yet (e.g. rows written before the directory existed).</summary>
        private static string? ResolveName(IReadOnlyDictionary<string, string?> currentNames, string? costCenterId, string? frozenName)
            => costCenterId is not null && currentNames.TryGetValue(costCenterId, out var current) && current is not null
                ? current
                : frozenName;

        public async Task<IReadOnlyList<UserMonthTotal>> GetUserTotalsAsync(int year, int month, UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var q = ApplyScope(db.UsageSnapshots.Where(x => x.Year == year && x.Month == month), scope);
            // Materialize the (scope-filtered, single-month) rows then aggregate in memory so the
            // query works across EF providers (SQL Server + the local-dev in-memory provider).
            var rows = await q.ToListAsync(ct);
            var currentNames = await LoadCurrentNamesAsync(db, ct);
            return rows.GroupBy(x => new { x.UserLogin, x.UserName, x.CostCenterId, x.CostCenterName })
                .Select(g => new UserMonthTotal(g.Key.UserLogin, g.Key.UserName, g.Key.CostCenterId,
                    ResolveName(currentNames, g.Key.CostCenterId, g.Key.CostCenterName), g.Sum(v => v.NetAmount), g.Sum(v => v.GrossAmount)))
                .OrderByDescending(r => r.NetAmount).ToList();
        }

        public sealed record UserMonthPage(
            IReadOnlyList<UserMonthTotal> Items,
            int MatchingUserCount,
            int TotalUserCount,
            decimal TotalSpend,
            decimal TotalGrossSpend,
            decimal MaxUserNet,
            UserMonthTotal? TopUser);

        /// <summary>
        /// Search + page the per-user monthly breakdown entirely in the database: the GROUP BY, search
        /// filter, ORDER BY, and OFFSET/FETCH all execute in SQL (translated by the EF Core SqlServer
        /// provider; the local-dev InMemory provider runs the same LINQ client-side against its store).
        /// Only <paramref name="pageSize"/> rows are ever materialized into app memory, regardless of how
        /// many users or raw usage rows exist for the month — this is what lets the per-user table scale
        /// to hundreds of users without loading every row on every page view.
        /// </summary>
        public async Task<UserMonthPage> GetUserTotalsPagedAsync(
            int year, int month, UserScope scope, string? search, int page, int pageSize, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var scoped = ApplyScope(db.UsageSnapshots.Where(x => x.Year == year && x.Month == month), scope);

            var grouped = scoped
                .GroupBy(x => new { x.UserLogin, x.UserName, x.CostCenterId, x.CostCenterName })
                .Select(g => new
                {
                    g.Key.UserLogin,
                    g.Key.UserName,
                    g.Key.CostCenterId,
                    g.Key.CostCenterName,
                    NetAmount = g.Sum(v => v.NetAmount),
                    GrossAmount = g.Sum(v => v.GrossAmount)
                });

            var filtered = grouped;
            var term = search?.Trim();
            if (!string.IsNullOrEmpty(term))
            {
                var lowered = term.ToLower();
                filtered = grouped.Where(u =>
                    u.UserLogin.ToLower().Contains(lowered) ||
                    (u.UserName != null && u.UserName.ToLower().Contains(lowered)) ||
                    (u.CostCenterName != null && u.CostCenterName.ToLower().Contains(lowered)) ||
                    (u.CostCenterId != null && u.CostCenterId.ToLower().Contains(lowered)));
            }

            var matchingUserCount = await filtered.CountAsync(ct);

            var pageRows = await filtered
                .OrderByDescending(u => u.NetAmount)
                .Skip(Math.Max(0, (page - 1) * pageSize))
                .Take(pageSize)
                .ToListAsync(ct);
            var currentNames = await LoadCurrentNamesAsync(db, ct);
            var items = pageRows
                .Select(r => new UserMonthTotal(r.UserLogin, r.UserName, r.CostCenterId, ResolveName(currentNames, r.CostCenterId, r.CostCenterName), r.NetAmount, r.GrossAmount))
                .ToList();

            // Month-level KPIs (total spend, top user, distinct user count) are independent of the
            // search term and current page — compute them from the full scoped rows via aggregate
            // queries so they never require materializing the whole per-user list.
            var totalSpend = await scoped.SumAsync(x => x.NetAmount, ct);
            var totalGrossSpend = await scoped.SumAsync(x => x.GrossAmount, ct);
            var totalUserCount = await scoped.Select(x => x.UserLogin).Distinct().CountAsync(ct);
            var maxUserNet = totalUserCount > 0
                ? await grouped.MaxAsync(u => u.NetAmount, ct)
                : 0m;
            var topRow = await grouped.OrderByDescending(u => u.NetAmount).FirstOrDefaultAsync(ct);
            var topUser = topRow is null
                ? null
                : new UserMonthTotal(topRow.UserLogin, topRow.UserName, topRow.CostCenterId, ResolveName(currentNames, topRow.CostCenterId, topRow.CostCenterName), topRow.NetAmount, topRow.GrossAmount);

            return new UserMonthPage(items, matchingUserCount, totalUserCount, totalSpend, totalGrossSpend, maxUserNet, topUser);
        }

        public async Task<IReadOnlyList<CostCenterTotal>> GetCostCenterTotalsAsync(int year, int month, UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var q = ApplyScope(db.UsageSnapshots.Where(x => x.Year == year && x.Month == month), scope);
            var rows = await q.ToListAsync(ct);
            var currentNames = await LoadCurrentNamesAsync(db, ct);
            // Group by id only (not the (id, name) pair) so a rename mid-history doesn't split one
            // cost center's total across two rows — see CostCenterDirectoryEntry.
            return rows.GroupBy(x => x.CostCenterId)
                .Select(g => new CostCenterTotal(g.Key, ResolveName(currentNames, g.Key, g.OrderByDescending(v => v.SnapshotUtc).First().CostCenterName), g.Sum(v => v.NetAmount), g.Sum(v => v.GrossAmount)))
                .OrderByDescending(r => r.NetAmount).ToList();
        }

        /// <summary>Net spend grouped by model for a single month (drives the model breakdown card).</summary>
        public async Task<IReadOnlyList<ModelTotal>> GetModelTotalsAsync(int year, int month, UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var q = ApplyScope(db.UsageSnapshots.Where(x => x.Year == year && x.Month == month), scope);
            var rows = await q.ToListAsync(ct);
            return rows.GroupBy(x => x.Model)
                .Select(g => new ModelTotal(string.IsNullOrWhiteSpace(g.Key) ? "—" : g.Key, g.Sum(v => v.NetAmount), g.Sum(v => v.GrossAmount)))
                .OrderByDescending(r => r.NetAmount).ToList();
        }

        /// <summary>Total net spend for a single month within scope (used for the month-over-month KPI delta).</summary>
        public async Task<decimal> GetMonthTotalAsync(int year, int month, UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var q = ApplyScope(db.UsageSnapshots.Where(x => x.Year == year && x.Month == month), scope);
            var rows = await q.Select(x => x.NetAmount).ToListAsync(ct);
            return rows.Sum();
        }

        public async Task<IReadOnlyList<TrendPoint>> GetTrendAsync(int months, UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var q = ApplyScope(db.UsageSnapshots, scope);
            var rows = await q.ToListAsync(ct);
            var all = rows.GroupBy(x => new { x.Year, x.Month })
                .Select(g => new TrendPoint(g.Key.Year, g.Key.Month, g.Sum(v => v.NetAmount)))
                .OrderBy(r => r.Year).ThenBy(r => r.Month)
                .ToList();
            // months <= 0 => all history; otherwise keep the most recent N points.
            return months > 0 && all.Count > months ? all.Skip(all.Count - months).ToList() : all;
        }

        // ── Multi-dimensional reporting ────────────────────────────────────────────
        public enum SeriesDimension { Total, User, Model, CostCenter }
        public enum TimeGranularity { Day, Week, Month }

        public sealed record SeriesPoint(DateOnly BucketStart, string Label, decimal NetAmount);
        public sealed record Series(string Key, IReadOnlyList<SeriesPoint> Points, decimal Total);
        public sealed record UserOption(string Login, string? Name);
        public sealed record FilterOptions(IReadOnlyList<UserOption> Users, IReadOnlyList<string> Models, IReadOnlyList<CostCenterTotal> CostCenters);

        /// <summary>Distinct users / models / cost centers within the caller's scope — drives the report filter dropdowns.</summary>
        public async Task<FilterOptions> GetFilterOptionsAsync(UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await ApplyScope(db.UsageSnapshots, scope).ToListAsync(ct);
            var currentNames = await LoadCurrentNamesAsync(db, ct);
            var users = rows.GroupBy(x => new { x.UserLogin, x.UserName })
                .Select(g => new UserOption(g.Key.UserLogin, g.Key.UserName))
                .OrderBy(u => u.Name ?? u.Login).ToList();
            var models = rows.Select(x => x.Model).Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct().OrderBy(m => m).ToList();
            var ccs = rows.GroupBy(x => x.CostCenterId)
                .Select(g => new CostCenterTotal(g.Key, ResolveName(currentNames, g.Key, g.OrderByDescending(v => v.SnapshotUtc).First().CostCenterName), g.Sum(v => v.NetAmount)))
                .OrderBy(c => c.CostCenterName ?? c.CostCenterId).ToList();
            return new FilterOptions(users, models, ccs);
        }

        private static DateOnly BucketOf(UsageSnapshot r, TimeGranularity gran)
        {
            var d = new DateOnly(r.Year, r.Month, Math.Clamp(r.Day <= 0 ? 1 : r.Day, 1, DateTime.DaysInMonth(r.Year, r.Month)));
            return gran switch
            {
                TimeGranularity.Day => d,
                TimeGranularity.Week => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)), // Monday-start week
                _ => new DateOnly(r.Year, r.Month, 1)
            };
        }

        private static string LabelOf(DateOnly bucketStart, TimeGranularity gran) => gran switch
        {
            TimeGranularity.Day => bucketStart.ToString("MMM d"),
            TimeGranularity.Week => "wk " + bucketStart.ToString("MMM d"),
            _ => bucketStart.ToString("MMM yy")
        };

        /// <summary>
        /// Time-series grouped by a dimension (total / user / model / cost center) and bucketed by
        /// day, week or month, with optional pinned filters that compose. Keeps the most recent
        /// <paramref name="count"/> buckets (0 = all). All buckets appear on every series (zero-filled)
        /// so lines align. Non-total dimensions are capped to the top <paramref name="topN"/> by spend
        /// (remainder → "Other").
        /// </summary>
        public async Task<IReadOnlyList<Series>> GetSeriesAsync(
            SeriesDimension dim, TimeGranularity gran, int count,
            string? filterUser, string? filterModel, string? filterCostCenter,
            UserScope scope, int topN = 8, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var q = ApplyScope(db.UsageSnapshots, scope);
            if (!string.IsNullOrWhiteSpace(filterUser)) q = q.Where(x => x.UserLogin == filterUser);
            if (!string.IsNullOrWhiteSpace(filterModel)) q = q.Where(x => x.Model == filterModel);
            if (!string.IsNullOrWhiteSpace(filterCostCenter)) q = q.Where(x => x.CostCenterId == filterCostCenter);
            var rows = await q.ToListAsync(ct);
            var currentNames = dim == SeriesDimension.CostCenter ? await LoadCurrentNamesAsync(db, ct) : new Dictionary<string, string?>();

            // bucket every row, then choose the window of most-recent buckets
            var bucketed = rows.Select(r => (Bucket: BucketOf(r, gran), Row: r)).ToList();
            var allBuckets = bucketed.Select(b => b.Bucket).Distinct().OrderBy(b => b).ToList();
            if (count > 0 && allBuckets.Count > count) allBuckets = allBuckets.Skip(allBuckets.Count - count).ToList();
            var bucketSet = allBuckets.ToHashSet();
            var win = bucketed.Where(b => bucketSet.Contains(b.Bucket)).ToList();

            // Key by cost-center id (not name) so a rename mid-history doesn't split one cost center's
            // trend line into two series; the display label still resolves to the CURRENT name.
            Func<UsageSnapshot, string> keySel = dim switch
            {
                SeriesDimension.User => r => r.UserName ?? r.UserLogin,
                SeriesDimension.Model => r => string.IsNullOrWhiteSpace(r.Model) ? "—" : r.Model,
                SeriesDimension.CostCenter => r => r.CostCenterId ?? "—",
                _ => _ => "Total"
            };
            Func<string, string> labelSel = dim == SeriesDimension.CostCenter
                ? key => (key != "—" && currentNames.TryGetValue(key, out var n) && n is not null) ? n : key
                : key => key;

            Series BuildSeries(string key, IEnumerable<(DateOnly Bucket, UsageSnapshot Row)> items)
            {
                var byBucket = items.GroupBy(i => i.Bucket).ToDictionary(g => g.Key, g => g.Sum(v => v.Row.NetAmount));
                var pts = allBuckets.Select(b => new SeriesPoint(b, LabelOf(b, gran), byBucket.TryGetValue(b, out var v) ? v : 0m)).ToList();
                return new Series(labelSel(key), pts, pts.Sum(p => p.NetAmount));
            }

            if (dim == SeriesDimension.Total)
                return new[] { BuildSeries("Total", win) };

            var series = win.GroupBy(b => keySel(b.Row)).Select(g => BuildSeries(g.Key, g))
                .OrderByDescending(s => s.Total).ToList();

            if (series.Count > topN)
            {
                var top = series.Take(topN).ToList();
                var rest = series.Skip(topN).ToList();
                var otherPts = allBuckets.Select((b, i) => new SeriesPoint(b, LabelOf(b, gran), rest.Sum(s => s.Points[i].NetAmount))).ToList();
                top.Add(new Series("Other", otherPts, otherPts.Sum(p => p.NetAmount)));
                return top;
            }
            return series;
        }

    }
}
