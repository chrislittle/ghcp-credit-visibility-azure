using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Authorization;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>Scope-aware read model for the dashboard. Reads ONLY from the database
    /// (populated by the snapshot job) and filters every query to the caller's
    /// <see cref="UserScope"/> so a manager sees only their people. Scope is enforced as
    /// exact (enterprise, cost-center) PAIRS — never bare cost-center ids — and every
    /// query honors the scope's optional enterprise filter (the UI dropdown).</summary>
    public sealed class UsageQueryService
    {
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        public UsageQueryService(IDbContextFactory<BillingDbContext> dbFactory) => _dbFactory = dbFactory;

        // GrossAmount is carried alongside NetAmount so the dashboard can optionally show real
        // usage activity even when it's fully covered by an included allowance (NetAmount = 0) —
        // see the "Dashboard:ShowGrossUsage" feature flag.
        public sealed record UserMonthTotal(string UserLogin, string? UserName, string? CostCenterId, string? CostCenterName, decimal NetAmount, decimal GrossAmount = 0m, long EnterpriseId = 0, string? EnterpriseName = null);
        public sealed record CostCenterTotal(string? CostCenterId, string? CostCenterName, decimal NetAmount, decimal GrossAmount = 0m, long EnterpriseId = 0, string? EnterpriseName = null);
        public sealed record ModelTotal(string Model, decimal NetAmount, decimal GrossAmount = 0m);
        public sealed record TrendPoint(int Year, int Month, decimal NetAmount);
        public sealed record MonthOption(int Year, int Month);
        public sealed record EnterpriseOption(long Id, string Name);

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

        /// <summary>The enterprises whose data the caller can see (drives the UI's enterprise filter;
        /// hidden when only one). SeesAll → every registered enterprise with any visibility value.</summary>
        public async Task<IReadOnlyList<EnterpriseOption>> GetVisibleEnterprisesAsync(UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var names = await LoadEnterpriseNamesAsync(db, ct);
            if (scope.SeesAll)
            {
                return names.OrderBy(kv => kv.Key)
                    .Select(kv => new EnterpriseOption(kv.Key, kv.Value))
                    .ToList();
            }
            return scope.EnterpriseIds.OrderBy(id => id)
                .Select(id => new EnterpriseOption(id, names.GetValueOrDefault(id, $"enterprise {id}")))
                .ToList();
        }

        private static IQueryable<UsageSnapshot> ApplyScope(IQueryable<UsageSnapshot> q, UserScope scope)
        {
            // The enterprise filter narrows ANY scope (including admins' SeesAll) to one enterprise.
            if (scope.EnterpriseFilter is long entFilter) q = q.Where(x => x.EnterpriseId == entFilter);
            if (scope.SeesAll) return q;
            var hasPairs = scope.CostCenters.Count > 0;
            var hasUsers = scope.UserLogins.Count > 0;
            if (!hasPairs && !hasUsers) return q.Where(_ => false); // no access
            if (hasPairs) q = q.Where(BuildPairPredicate(scope.CostCenters));
            if (hasUsers) q = q.Where(x => scope.UserLogins.Contains(x.UserLogin));
            return q;
        }

        /// <summary>
        /// Builds the exact (enterprise, cost-center) pair filter as an OR of per-enterprise
        /// id-list Contains clauses, so it translates to SQL. A flat Contains over bare cost-center
        /// ids would be wider than the grant whenever an id existed in two enterprises.
        /// </summary>
        private static Expression<Func<UsageSnapshot, bool>> BuildPairPredicate(IReadOnlyCollection<EnterpriseCostCenter> pairs)
        {
            var p = Expression.Parameter(typeof(UsageSnapshot), "x");
            Expression? body = null;
            foreach (var g in pairs.GroupBy(x => x.EnterpriseId))
            {
                var ids = g.Select(x => x.CostCenterId).ToList();
                var entEq = Expression.Equal(
                    Expression.Property(p, nameof(UsageSnapshot.EnterpriseId)),
                    Expression.Constant(g.Key));
                var ccProp = Expression.Property(p, nameof(UsageSnapshot.CostCenterId));
                var notNull = Expression.NotEqual(ccProp, Expression.Constant(null, typeof(string)));
                var contains = Expression.Call(
                    typeof(Enumerable), nameof(Enumerable.Contains), new[] { typeof(string) },
                    Expression.Constant(ids), ccProp);
                var clause = Expression.AndAlso(entEq, Expression.AndAlso(notNull, contains));
                body = body is null ? clause : Expression.OrElse(body, clause);
            }
            return Expression.Lambda<Func<UsageSnapshot, bool>>(body ?? Expression.Constant(false), p);
        }

        /// <summary>Loads the cost-center directory ((enterprise, id) -> CURRENT name, refreshed every
        /// snapshot run) so display names stay current even for historical rows that froze an old name.</summary>
        private static async Task<Dictionary<(long EnterpriseId, string CostCenterId), string?>> LoadCurrentNamesAsync(BillingDbContext db, CancellationToken ct)
        {
            var rows = await db.CostCenterDirectory.ToListAsync(ct);
            return rows.ToDictionary(x => (x.EnterpriseId, x.CostCenterId), x => x.CurrentName);
        }

        /// <summary>Id → display name (falls back to slug) for registered enterprises.</summary>
        private static async Task<Dictionary<long, string>> LoadEnterpriseNamesAsync(BillingDbContext db, CancellationToken ct)
        {
            var rows = await db.Enterprises.ToListAsync(ct);
            return rows.ToDictionary(e => e.Id, e => string.IsNullOrWhiteSpace(e.DisplayName) ? e.Slug : e.DisplayName!);
        }

        /// <summary>Prefers the directory's current name over the frozen per-row name; falls back to the
        /// frozen name if the (enterprise, id) isn't in the directory yet (e.g. rows written before the directory existed).</summary>
        private static string? ResolveName(IReadOnlyDictionary<(long, string), string?> currentNames, long enterpriseId, string? costCenterId, string? frozenName)
            => costCenterId is not null && currentNames.TryGetValue((enterpriseId, costCenterId), out var current) && current is not null
                ? current
                : frozenName;

        public async Task<IReadOnlyList<UserMonthTotal>> GetUserTotalsAsync(int year, int month, UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var q = ApplyScope(db.UsageSnapshots.Where(x => x.Year == year && x.Month == month), scope);
            // Materialize the (scope-filtered, single-month) rows then aggregate in memory so the
            // query works across EF providers (SQL Server + the local-dev in-memory provider).
            // The group key INCLUDES EnterpriseId: the same login in two enterprises is two rows,
            // matching how GitHub bills — merging would hide which enterprise's budget the spend hits.
            var rows = await q.ToListAsync(ct);
            var currentNames = await LoadCurrentNamesAsync(db, ct);
            var entNames = await LoadEnterpriseNamesAsync(db, ct);
            return rows.GroupBy(x => new { x.EnterpriseId, x.UserLogin, x.UserName, x.CostCenterId, x.CostCenterName })
                .Select(g => new UserMonthTotal(g.Key.UserLogin, g.Key.UserName, g.Key.CostCenterId,
                    ResolveName(currentNames, g.Key.EnterpriseId, g.Key.CostCenterId, g.Key.CostCenterName),
                    g.Sum(v => v.NetAmount), g.Sum(v => v.GrossAmount),
                    g.Key.EnterpriseId, entNames.GetValueOrDefault(g.Key.EnterpriseId)))
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
                .GroupBy(x => new { x.EnterpriseId, x.UserLogin, x.UserName, x.CostCenterId, x.CostCenterName })
                .Select(g => new
                {
                    g.Key.EnterpriseId,
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
            var entNames = await LoadEnterpriseNamesAsync(db, ct);
            var items = pageRows
                .Select(r => new UserMonthTotal(r.UserLogin, r.UserName, r.CostCenterId,
                    ResolveName(currentNames, r.EnterpriseId, r.CostCenterId, r.CostCenterName),
                    r.NetAmount, r.GrossAmount, r.EnterpriseId, entNames.GetValueOrDefault(r.EnterpriseId)))
                .ToList();

            // Month-level KPIs (total spend, top user, distinct user count) are independent of the
            // search term and current page — compute them from the full scoped rows via aggregate
            // queries so they never require materializing the whole per-user list.
            var totalSpend = await scoped.SumAsync(x => x.NetAmount, ct);
            var totalGrossSpend = await scoped.SumAsync(x => x.GrossAmount, ct);
            // Distinct (enterprise, login): the same login in two enterprises is two billed seats.
            var totalUserCount = await scoped.Select(x => new { x.EnterpriseId, x.UserLogin }).Distinct().CountAsync(ct);
            var maxUserNet = totalUserCount > 0
                ? await grouped.MaxAsync(u => u.NetAmount, ct)
                : 0m;
            var topRow = await grouped.OrderByDescending(u => u.NetAmount).FirstOrDefaultAsync(ct);
            var topUser = topRow is null
                ? null
                : new UserMonthTotal(topRow.UserLogin, topRow.UserName, topRow.CostCenterId,
                    ResolveName(currentNames, topRow.EnterpriseId, topRow.CostCenterId, topRow.CostCenterName),
                    topRow.NetAmount, topRow.GrossAmount, topRow.EnterpriseId, entNames.GetValueOrDefault(topRow.EnterpriseId));

            return new UserMonthPage(items, matchingUserCount, totalUserCount, totalSpend, totalGrossSpend, maxUserNet, topUser);
        }

        public async Task<IReadOnlyList<CostCenterTotal>> GetCostCenterTotalsAsync(int year, int month, UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var q = ApplyScope(db.UsageSnapshots.Where(x => x.Year == year && x.Month == month), scope);
            var rows = await q.ToListAsync(ct);
            var currentNames = await LoadCurrentNamesAsync(db, ct);
            var entNames = await LoadEnterpriseNamesAsync(db, ct);
            // Group by (enterprise, id) — never by name: renames would split totals, and two
            // enterprises' identically-NAMED cost centers must never merge.
            return rows.GroupBy(x => new { x.EnterpriseId, x.CostCenterId })
                .Select(g => new CostCenterTotal(g.Key.CostCenterId,
                    ResolveName(currentNames, g.Key.EnterpriseId, g.Key.CostCenterId, g.OrderByDescending(v => v.SnapshotUtc).First().CostCenterName),
                    g.Sum(v => v.NetAmount), g.Sum(v => v.GrossAmount),
                    g.Key.EnterpriseId, entNames.GetValueOrDefault(g.Key.EnterpriseId)))
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
        public enum SeriesDimension { Total, User, Model, CostCenter, Enterprise }
        public enum TimeGranularity { Day, Week, Month }

        public sealed record SeriesPoint(DateOnly BucketStart, string Label, decimal NetAmount);
        public sealed record Series(string Key, IReadOnlyList<SeriesPoint> Points, decimal Total);
        public sealed record UserOption(string Login, string? Name);
        public sealed record CostCenterFilterOption(long EnterpriseId, string CostCenterId, string Label);
        public sealed record FilterOptions(IReadOnlyList<UserOption> Users, IReadOnlyList<string> Models, IReadOnlyList<CostCenterFilterOption> CostCenters, IReadOnlyList<EnterpriseOption> Enterprises);

        /// <summary>Distinct users / models / cost centers / enterprises within the caller's scope — drives the report filter dropdowns.</summary>
        public async Task<FilterOptions> GetFilterOptionsAsync(UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await ApplyScope(db.UsageSnapshots, scope).ToListAsync(ct);
            var currentNames = await LoadCurrentNamesAsync(db, ct);
            var entNames = await LoadEnterpriseNamesAsync(db, ct);
            var multiEnterprise = rows.Select(x => x.EnterpriseId).Distinct().Count() > 1;
            var users = rows.GroupBy(x => new { x.UserLogin, x.UserName })
                .Select(g => new UserOption(g.Key.UserLogin, g.Key.UserName))
                .OrderBy(u => u.Name ?? u.Login).ToList();
            var models = rows.Select(x => x.Model).Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct().OrderBy(m => m).ToList();
            var ccs = rows.Where(x => x.CostCenterId != null)
                .GroupBy(x => new { x.EnterpriseId, x.CostCenterId })
                .Select(g =>
                {
                    var name = ResolveName(currentNames, g.Key.EnterpriseId, g.Key.CostCenterId, g.OrderByDescending(v => v.SnapshotUtc).First().CostCenterName)
                               ?? g.Key.CostCenterId!;
                    // Qualify with the enterprise whenever more than one is in play — two
                    // enterprises WILL both have an "Engineering".
                    var label = multiEnterprise ? $"{name} — {entNames.GetValueOrDefault(g.Key.EnterpriseId, $"enterprise {g.Key.EnterpriseId}")}" : name;
                    return new CostCenterFilterOption(g.Key.EnterpriseId, g.Key.CostCenterId!, label);
                })
                .OrderBy(c => c.Label).ToList();
            var enterprises = rows.Select(x => x.EnterpriseId).Distinct().OrderBy(id => id)
                .Select(id => new EnterpriseOption(id, entNames.GetValueOrDefault(id, $"enterprise {id}")))
                .ToList();
            return new FilterOptions(users, models, ccs, enterprises);
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
        /// Time-series grouped by a dimension (total / user / model / cost center / enterprise) and
        /// bucketed by day, week or month, with optional pinned filters that compose. Keeps the most
        /// recent <paramref name="count"/> buckets (0 = all). All buckets appear on every series
        /// (zero-filled) so lines align. Non-total dimensions are capped to the top
        /// <paramref name="topN"/> by spend (remainder → "Other").
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
            if (!string.IsNullOrWhiteSpace(filterCostCenter))
            {
                // Cost-center filter values are enterprise-qualified ("<enterpriseId>:<ccId>"), with a
                // bare-id fallback for old bookmarked URLs.
                var (fEnt, fCc) = ParseCostCenterKey(filterCostCenter);
                q = fEnt is long fe
                    ? q.Where(x => x.EnterpriseId == fe && x.CostCenterId == fCc)
                    : q.Where(x => x.CostCenterId == fCc);
            }
            var rows = await q.ToListAsync(ct);
            var needsNames = dim is SeriesDimension.CostCenter or SeriesDimension.Enterprise;
            var currentNames = dim == SeriesDimension.CostCenter ? await LoadCurrentNamesAsync(db, ct) : new Dictionary<(long, string), string?>();
            var entNames = needsNames || rows.Select(x => x.EnterpriseId).Distinct().Count() > 1
                ? await LoadEnterpriseNamesAsync(db, ct)
                : new Dictionary<long, string>();
            var multiEnterprise = rows.Select(x => x.EnterpriseId).Distinct().Count() > 1;

            // bucket every row, then choose the window of most-recent buckets
            var bucketed = rows.Select(r => (Bucket: BucketOf(r, gran), Row: r)).ToList();
            var allBuckets = bucketed.Select(b => b.Bucket).Distinct().OrderBy(b => b).ToList();
            if (count > 0 && allBuckets.Count > count) allBuckets = allBuckets.Skip(allBuckets.Count - count).ToList();
            var bucketSet = allBuckets.ToHashSet();
            var win = bucketed.Where(b => bucketSet.Contains(b.Bucket)).ToList();

            // Key by (enterprise, cost-center id) — not by name — so a rename mid-history doesn't
            // split one cost center's trend line into two series, and two enterprises' same-named
            // cost centers never merge; the display label still resolves to the CURRENT name.
            Func<UsageSnapshot, string> keySel = dim switch
            {
                SeriesDimension.User => r => (r.UserName ?? r.UserLogin) + (multiEnterprise ? "\u001f" + r.EnterpriseId : ""),
                SeriesDimension.Model => r => string.IsNullOrWhiteSpace(r.Model) ? "—" : r.Model,
                SeriesDimension.CostCenter => r => r.CostCenterId is null ? "—" : r.EnterpriseId + ":" + r.CostCenterId,
                SeriesDimension.Enterprise => r => r.EnterpriseId.ToString(),
                _ => _ => "Total"
            };
            Func<string, string> labelSel = dim switch
            {
                SeriesDimension.CostCenter => key =>
                {
                    if (key == "—") return key;
                    var (entId, ccId) = ParseCostCenterKey(key);
                    var name = entId is long e2 ? (ResolveName(currentNames, e2, ccId, null) ?? ccId) : ccId;
                    return multiEnterprise && entId is long e3
                        ? $"{name} — {entNames.GetValueOrDefault(e3, $"enterprise {e3}")}"
                        : name;
                },
                SeriesDimension.Enterprise => key =>
                    long.TryParse(key, out var id) ? entNames.GetValueOrDefault(id, $"enterprise {id}") : key,
                SeriesDimension.User => key =>
                {
                    var i = key.IndexOf('\u001f');
                    if (i < 0) return key;
                    var name = key[..i];
                    return long.TryParse(key[(i + 1)..], out var id)
                        ? $"{name} — {entNames.GetValueOrDefault(id, $"enterprise {id}")}"
                        : name;
                },
                _ => key => key
            };

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

        /// <summary>Parses "&lt;enterpriseId&gt;:&lt;ccId&gt;"; a value with no enterprise prefix
        /// (old bookmarked URL) yields (null, value).</summary>
        public static (long? EnterpriseId, string CostCenterId) ParseCostCenterKey(string value)
        {
            var i = value.IndexOf(':');
            if (i > 0 && long.TryParse(value[..i], out var entId)) return (entId, value[(i + 1)..]);
            return (null, value);
        }
    }
}
