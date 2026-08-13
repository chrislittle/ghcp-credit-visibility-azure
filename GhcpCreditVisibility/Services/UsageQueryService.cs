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
        /// <summary>PrevMonthNetAmount: the same (enterprise, login)'s total for the previous month —
        /// null when the user had no rows then. Only populated by the paged dashboard query, and only
        /// meaningful when the page reports the previous month has data at all.</summary>
        public sealed record UserMonthTotal(string UserLogin, string? UserName, string? CostCenterId, string? CostCenterName, decimal NetAmount, decimal GrossAmount = 0m, long EnterpriseId = 0, string? EnterpriseName = null, decimal? PrevMonthNetAmount = null);
        public sealed record CostCenterTotal(string? CostCenterId, string? CostCenterName, decimal NetAmount, decimal GrossAmount = 0m, long EnterpriseId = 0, string? EnterpriseName = null);
        public sealed record ModelTotal(string Model, decimal NetAmount, decimal GrossAmount = 0m);
        public sealed record TrendPoint(int Year, int Month, decimal NetAmount);
        /// <summary>
        /// Relationship between what was CONSUMED (gross) and what is BILLABLE (net) once the
        /// included allowance is applied.
        ///
        /// This exists because of a genuinely misleading display: a month in which every user stays
        /// inside their allowance has <c>netAmount</c> summing to ZERO while real consumption
        /// happened — confirmed against the live API. Any view that shows only net renders that as
        /// "$0.00", which is indistinguishable from nobody using Copilot at all, and invites the
        /// conclusion that a rollout failed when usage is in fact healthy and simply covered.
        ///
        /// Net stays the headline everywhere — it is the number finance reconciles against. This
        /// type supplies the context that stops a zero being read as inactivity.
        /// </summary>
        public sealed record AllowanceCoverage(decimal Net, decimal Gross)
        {
            /// <summary>Value absorbed by the included allowance. Never negative: a gross below net
            /// would mean a surcharge rather than a discount, which is not a case to invent.</summary>
            public decimal Covered => Gross > Net ? Gross - Net : 0m;

            /// <summary>The misleading case: real consumption, nothing billable.</summary>
            public bool IsFullyCovered => Net <= 0m && Gross > 0m;

            /// <summary>Partly absorbed — billable, but understating consumption.</summary>
            public bool IsPartiallyCovered => Net > 0m && Covered > 0m;

            /// <summary>Share of consumption the allowance absorbed (0 when nothing was consumed).</summary>
            public double CoveredPct => Gross > 0m ? (double)(Covered / Gross) * 100.0 : 0.0;

            /// <summary>True when there was genuinely no activity — as opposed to activity that cost
            /// nothing. The distinction this whole type exists to preserve.</summary>
            public bool IsGenuinelyIdle => Gross <= 0m && Net <= 0m;
        }

        public sealed record MonthOption(int Year, int Month);

        /// <summary>
        /// When this deployment started collecting per-user usage for the viewer's scope — the
        /// earliest month that actually has data, or, when there is none yet, the earliest
        /// registration date among the enterprises in scope.
        ///
        /// Exists because per-user history starts empty. A newly onboarded enterprise has an empty
        /// dashboard until usage accrues — and "empty" is indistinguishable from "broken" without
        /// being told why.
        ///
        /// Also serves as the PROGRESS INDICATOR for the opt-in per-user backfill (see
        /// <see cref="SnapshotService"/>): that job fills WHOLE months, oldest boundary moving back
        /// one complete month at a time, so this date is accurate at every instant rather than only
        /// once the job finishes. Partial fills are avoided on purpose — an incomplete month would
        /// look like a month of genuinely low spend, which is worse than a visible gap in an app
        /// whose numbers people reconcile against invoices.
        /// </summary>
        public async Task<DateOnly?> GetCollectingSinceAsync(UserScope scope, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var months = await ApplyScope(db.UsageSnapshots, scope)
                .Select(x => new { x.Year, x.Month })
                .Distinct()
                .ToListAsync(ct);

            if (months.Count > 0)
            {
                var earliest = months.OrderBy(m => m.Year).ThenBy(m => m.Month).First();
                return new DateOnly(earliest.Year, earliest.Month, 1);
            }

            // No usage rows at all — fall back to when the enterprise(s) were registered, which is
            // the honest answer to "why is this empty": nothing has been collected yet.
            var entQuery = db.Enterprises.Where(e => e.Slug != Enterprise.BootstrapPlaceholderSlug);
            if (scope.EnterpriseFilter is long entFilter)
                entQuery = entQuery.Where(e => e.Id == entFilter);
            else if (!scope.SeesAll)
            {
                var visible = scope.EnterpriseIds.ToList();
                if (visible.Count == 0) return null;
                entQuery = entQuery.Where(e => visible.Contains(e.Id));
            }

            var created = await entQuery.Select(e => (DateTime?)e.CreatedUtc).MinAsync(ct);
            return created is DateTime c ? DateOnly.FromDateTime(c) : null;
        }
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

        /// <summary>Header scope pill content: <see cref="Label"/> is what the pill shows;
        /// <see cref="Detail"/> (when set) is the full resolved list, rendered as a tooltip.</summary>
        public sealed record ScopeDescription(string Label, string? Detail = null);

        /// <summary>
        /// Human-readable scope description for the header pill. Cost centers are shown by their
        /// CURRENT display name from the directory — never by raw id: real GitHub cost-center ids
        /// are GUIDs, and a non-admin's scope pill would otherwise read like a debug dump. Names are
        /// enterprise-qualified when the scope spans more than one enterprise. Up to two cost
        /// centers are named inline; beyond that the pill stays compact ("N cost centers across M
        /// enterprises") and the full list moves to the tooltip — an exec mapped to a dozen cost
        /// centers must not get a paragraph-length pill.
        /// </summary>
        public async Task<ScopeDescription> GetScopeDescriptionAsync(UserScope scope, CancellationToken ct = default)
        {
            if (scope.SeesAll) return new ScopeDescription("All cost centers");
            if (scope.ReadAllEnterpriseIds.Count == 0 && scope.CostCenters.Count == 0)
                return new ScopeDescription("No assigned scope");
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var currentNames = await LoadCurrentNamesAsync(db, ct);
            var entNames = await LoadEnterpriseNamesAsync(db, ct);

            // An Enterprise Reader grant is wider than any cost-center pair, so it leads the label.
            // Additional pair grants (a reader for one enterprise who also manages a cost center in
            // another) move to the tooltip rather than being dropped from the description entirely.
            if (scope.ReadAllEnterpriseIds.Count > 0)
            {
                var readNames = scope.ReadAllEnterpriseIds
                    .Select(id => entNames.GetValueOrDefault(id, $"enterprise {id}"))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var readLabel = readNames.Count <= 2
                    ? $"All cost centers · {string.Join(", ", readNames)}"
                    : $"All cost centers · {readNames.Count} enterprises";
                var readDetail = scope.CostCenters.Count > 0
                    ? $"{string.Join(", ", readNames)} — plus {scope.CostCenters.Count} additional cost center grant(s)"
                    : readNames.Count > 2 ? string.Join(", ", readNames) : null;
                return new ScopeDescription(readLabel, readDetail);
            }
            var enterpriseCount = scope.EnterpriseIds.Count;
            var multiEnterprise = enterpriseCount > 1;
            var labels = scope.CostCenters
                .Select(p =>
                {
                    var name = ResolveName(currentNames, p.EnterpriseId, p.CostCenterId, null) ?? p.CostCenterId;
                    return multiEnterprise
                        ? $"{name} · {entNames.GetValueOrDefault(p.EnterpriseId, $"enterprise {p.EnterpriseId}")}"
                        : name;
                })
                .Distinct()
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (labels.Count <= 2)
                return new ScopeDescription($"Cost centers: {string.Join(", ", labels)}");

            var summary = $"{labels.Count} cost centers" + (multiEnterprise ? $" across {enterpriseCount} enterprises" : "");
            return new ScopeDescription(summary, string.Join(", ", labels));
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

        /// <summary>
        /// Applies the viewer's scope to any usage-shaped table. Generic over the entity type so
        /// UsageSnapshot (monthly) and DailyUsageSnapshot (intra-month) share ONE implementation
        /// rather than two that can drift — this is an access-control filter, and two copies of an
        /// access-control filter is how one of them quietly stops matching the other.
        ///
        /// Every predicate is built against typeof(T)'s own properties by name, never through an
        /// interface member, so the expression trees stay translatable by the SQL Server provider.
        /// </summary>
        private static IQueryable<T> ApplyScope<T>(IQueryable<T> q, UserScope scope) where T : class
        {
            // The enterprise filter narrows ANY scope (including a global reader's SeesAll) to one enterprise.
            if (scope.EnterpriseFilter is long entFilter) q = q.Where(EqualsLong<T>("EnterpriseId", entFilter));
            if (scope.SeesAll) return q;
            var hasRead = scope.ReadAllEnterpriseIds.Count > 0;
            var hasPairs = scope.CostCenters.Count > 0;
            var hasUsers = scope.UserLogins.Count > 0;
            // An Enterprise Reader commonly holds NO cost-center pairs, so reader grants must be part
            // of this test — otherwise a perfectly valid reader falls through to "no access".
            if (!hasRead && !hasPairs && !hasUsers) return q.Where(_ => false); // no access
            if (hasRead || hasPairs) q = q.Where(BuildVisibilityPredicate<T>(scope.ReadAllEnterpriseIds, scope.CostCenters));
            if (hasUsers) q = q.Where(ContainsString<T>("UserLogin", scope.UserLogins));
            return q;
        }

        /// <summary>x =&gt; x.&lt;property&gt; == value, built against T's concrete property.</summary>
        private static Expression<Func<T, bool>> EqualsLong<T>(string property, long value)
        {
            var p = Expression.Parameter(typeof(T), "x");
            return Expression.Lambda<Func<T, bool>>(
                Expression.Equal(Expression.Property(p, property), Expression.Constant(value)), p);
        }

        /// <summary>x =&gt; values.Contains(x.&lt;property&gt;), built against T's concrete property.</summary>
        private static Expression<Func<T, bool>> ContainsString<T>(string property, IReadOnlyCollection<string> values)
        {
            var p = Expression.Parameter(typeof(T), "x");
            var list = values.ToList();
            var contains = typeof(List<string>).GetMethod(nameof(List<string>.Contains), new[] { typeof(string) })!;
            return Expression.Lambda<Func<T, bool>>(
                Expression.Call(Expression.Constant(list), contains, Expression.Property(p, property)), p);
        }

        /// <summary>
        /// Builds the viewer's visibility filter: enterprise-wide READER grants ORed with the exact
        /// (enterprise, cost-center) pair grants.
        ///
        /// Everything is an OR of per-enterprise id-list Contains clauses so it translates to SQL. A
        /// flat Contains over bare cost-center ids would be wider than the grant whenever an id
        /// existed in two enterprises — and the reader branch is deliberately a whole-enterprise
        /// clause, since an Enterprise Reader is defined as seeing every cost center within it,
        /// including rows whose CostCenterId is NULL (unattributed spend a pair grant can never match).
        /// </summary>
        private static Expression<Func<T, bool>> BuildVisibilityPredicate<T>(
            IReadOnlyCollection<long> readAllEnterpriseIds,
            IReadOnlyCollection<EnterpriseCostCenter> pairs)
        {
            var p = Expression.Parameter(typeof(T), "x");
            Expression? body = null;

            if (readAllEnterpriseIds.Count > 0)
            {
                var entIds = readAllEnterpriseIds.Distinct().ToList();
                body = Expression.Call(
                    typeof(Enumerable), nameof(Enumerable.Contains), new[] { typeof(long) },
                    Expression.Constant(entIds),
                    Expression.Property(p, nameof(UsageSnapshot.EnterpriseId)));
            }

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
            return Expression.Lambda<Func<T, bool>>(body ?? Expression.Constant(false), p);
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
            UserMonthTotal? TopUser,
            // False when the previous month has no rows in scope at all (first month of a
            // deployment / newly onboarded enterprise): per-user deltas would then flag EVERY
            // user as "new", which is noise — the UI renders em-dashes instead.
            bool HasPrevMonthData = false);

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

            // ── Previous-month per-user totals, for the "vs <prev month>" delta column ──
            // Same scope, previous calendar month, grouped by (enterprise, login) — the login alone
            // is NOT the identity (the same login in two enterprises is two billed seats). Only the
            // current page's logins are fetched.
            var prevMonthStart = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
            var prevScoped = ApplyScope(db.UsageSnapshots.Where(x => x.Year == prevMonthStart.Year && x.Month == prevMonthStart.Month), scope);
            var hasPrevMonthData = await prevScoped.AnyAsync(ct);
            var prevByUser = new Dictionary<(long EnterpriseId, string UserLogin), decimal>();
            if (hasPrevMonthData && pageRows.Count > 0)
            {
                var pageLogins = pageRows.Select(r => r.UserLogin).Distinct().ToList();
                var prevRows = await prevScoped
                    .Where(x => pageLogins.Contains(x.UserLogin))
                    .GroupBy(x => new { x.EnterpriseId, x.UserLogin })
                    .Select(g => new { g.Key.EnterpriseId, g.Key.UserLogin, Net = g.Sum(v => v.NetAmount) })
                    .ToListAsync(ct);
                prevByUser = prevRows.ToDictionary(r => (r.EnterpriseId, r.UserLogin), r => r.Net);
            }

            var items = pageRows
                .Select(r => new UserMonthTotal(r.UserLogin, r.UserName, r.CostCenterId,
                    ResolveName(currentNames, r.EnterpriseId, r.CostCenterId, r.CostCenterName),
                    r.NetAmount, r.GrossAmount, r.EnterpriseId, entNames.GetValueOrDefault(r.EnterpriseId),
                    prevByUser.TryGetValue((r.EnterpriseId, r.UserLogin), out var prevNet) ? prevNet : null))
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

            return new UserMonthPage(items, matchingUserCount, totalUserCount, totalSpend, totalGrossSpend, maxUserNet, topUser, hasPrevMonthData);
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

        // ── Included-allowance pool ─────────────────────────────────────────────────────────────

        /// <summary>
        /// One enterprise's included AI-credit allowance and how much of it this month has burned.
        ///
        /// Denominated in CREDITS, not dollars — the pool is a credit entitlement, and converting it
        /// to money would imply a billable amount that does not exist until the pool is exhausted.
        /// The only dollar figure here is <see cref="ProjectedOverageCredits"/>'s cost, which is
        /// genuinely billable.
        /// </summary>
        /// <summary>Seats on one Copilot plan, and what each is worth. <see cref="CreditsPerSeat"/>
        /// is null when the plan is not priced in configuration — those seats are real and must be
        /// reported, but cannot be added to capacity.</summary>
        public sealed record SeatPlan(string PlanType, int Seats, decimal? CreditsPerSeat)
        {
            public decimal? Credits => CreditsPerSeat is decimal c ? Seats * c : null;
        }

        /// <param name="Capacity">Null when no priced seats are known — collection has not run, failed,
        /// or every seat is on an unpriced plan. NEVER derived from the GHEC licence count.</param>
        /// <param name="Consumed">Null when no row carried a GrossQuantity — "not captured", NOT zero.</param>
        /// <param name="Plans">Per-plan seat breakdown, priced and unpriced alike.</param>
        public sealed record AllowancePool(
            long EnterpriseId,
            string EnterpriseName,
            IReadOnlyList<SeatPlan> Plans,
            decimal? Capacity,
            decimal? Consumed,
            bool HasIncompleteData,
            int DaysElapsed,
            int DaysInMonth,
            decimal? RunRatePerDay,
            decimal? ProjectedMonthEnd,
            int? ExhaustionDay)
        {
            /// <summary>Total assigned Copilot seats, priced or not. NOT the enterprise licence count.</summary>
            public int TotalSeats => Plans.Sum(p => p.Seats);

            /// <summary>Seats on a plan configuration does not price. Surfaced rather than dropped:
            /// silently excluding them understates capacity, which OVERSTATES the percentage used and
            /// manufactures false alarms.</summary>
            public int UnknownPlanSeats => Plans.Where(p => p.CreditsPerSeat is null).Sum(p => p.Seats);
            public IReadOnlyList<string> UnknownPlanTypes =>
                Plans.Where(p => p.CreditsPerSeat is null).Select(p => p.PlanType).ToList();
            public bool HasUnpricedPlans => UnknownPlanSeats > 0;

            public bool IsComputable => Capacity is > 0 && Consumed is not null;
            public double PctUsed => IsComputable ? (double)(Consumed!.Value / Capacity!.Value) * 100.0 : 0;
            /// <summary>Where the month is, as a percentage — the pace marker the meter compares against.
            /// 60% burned on day 3 and on day 28 are opposite situations; the raw figure cannot say which.</summary>
            public double PctElapsed => DaysInMonth > 0 ? DaysElapsed / (double)DaysInMonth * 100.0 : 0;
            public decimal? Remaining => IsComputable ? Capacity!.Value - Consumed!.Value : null;
            public bool IsExhausted => IsComputable && Consumed!.Value >= Capacity!.Value;
            public bool IsProjectedToExceed => !IsExhausted && IsComputable && ProjectedMonthEnd > Capacity;
            public decimal? ProjectedOverageCredits =>
                IsComputable && ProjectedMonthEnd > Capacity ? ProjectedMonthEnd - Capacity : null;
            /// <summary>Matches the budget meter's level classes so the two read as siblings.</summary>
            public string Level => !IsComputable ? "ok" : IsExhausted ? "over" : IsProjectedToExceed ? "critical" : "ok";
        }

        /// <summary>
        /// The included-allowance pool per enterprise for the CURRENT month.
        ///
        /// Current month only, deliberately. A burn-down of a closed month has nothing to project, and
        /// <see cref="EnterpriseCopilotSeat"/> holds CURRENT seat counts with no history — using
        /// today's seats to compute a past month's capacity would be quietly wrong. For a closed
        /// month, any net spend at all already proves the pool was exhausted.
        ///
        /// Capacity comes from ASSIGNED COPILOT SEATS, summed per plan. It must never fall back to
        /// <see cref="Enterprise.LicensedUserCount"/>: that is the GHEC licence count, a larger and
        /// different population (8 licences against 3 seats on a live enterprise), and sizing the
        /// allowance from it overstated capacity 5.5x. Overstatement is the dangerous direction — an
        /// enterprise about to exhaust its pool renders as comfortable — so when seats are unknown
        /// this reports "not computable" rather than substituting a number that is merely available.
        ///
        /// ENTERPRISE-GRAIN ONLY: the pool is a whole-enterprise figure that cannot be narrowed to a
        /// cost center, exactly like the organization rollup. Cost-center managers get nothing.
        /// </summary>
        /// <param name="creditsPerSeatByPlan">Included credits per seat, keyed by GitHub's
        /// <c>plan_type</c> ("business" 1900, "enterprise" 3900). A plan absent from this map is
        /// reported as unpriced rather than dropped from the sum.</param>
        public async Task<IReadOnlyList<AllowancePool>> GetAllowancePoolsAsync(
            int year, int month, UserScope scope,
            IReadOnlyDictionary<string, decimal> creditsPerSeatByPlan, DateTime asOfUtc, CancellationToken ct = default)
        {
            if (creditsPerSeatByPlan.Count == 0) return Array.Empty<AllowancePool>();
            if (year != asOfUtc.Year || month != asOfUtc.Month) return Array.Empty<AllowancePool>();
            if (!scope.HasEnterpriseRead) return Array.Empty<AllowancePool>();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var entQuery = db.Enterprises.Where(e => e.Slug != Enterprise.BootstrapPlaceholderSlug);
            var allowed = scope.EnterpriseReadFilter();
            if (allowed is not null)
            {
                if (allowed.Count == 0) return Array.Empty<AllowancePool>();
                var allowedIds = allowed.ToList();
                entQuery = entQuery.Where(e => allowedIds.Contains(e.Id));
            }
            var enterprises = await entQuery.ToListAsync(ct);
            if (enterprises.Count == 0) return Array.Empty<AllowancePool>();

            var entIds = enterprises.Select(e => e.Id).ToList();

            // Whole-enterprise consumption: no cost-center narrowing, because the pool IS the whole
            // enterprise and a partial sum would understate how close it is to exhaustion. Only rows
            // metered in AI CREDITS count — mixing unit types is how a credit total silently absorbs
            // a quantity measured in something else.
            var rows = await db.UsageSnapshots
                .Where(x => x.Year == year && x.Month == month && entIds.Contains(x.EnterpriseId))
                .Where(x => x.UnitType == UsageUnitTypes.AiCredits)
                .Select(x => new { x.EnterpriseId, x.GrossQuantity })
                .ToListAsync(ct);

            // CURRENT month's seat rows only. Earlier months are retained as history for a capacity
            // trend, but capacity today is today's seats — reading the whole table would sum a
            // year's worth of monthly counts into a wildly inflated ceiling.
            var seatRows = await db.EnterpriseCopilotSeats
                .Where(x => entIds.Contains(x.EnterpriseId) && x.Year == year && x.Month == month)
                .Select(x => new { x.EnterpriseId, x.PlanType, x.Seats })
                .ToListAsync(ct);

            var daysInMonth = DateTime.DaysInMonth(year, month);
            var daysElapsed = Math.Clamp(asOfUtc.Day, 1, daysInMonth);

            var pools = new List<AllowancePool>();
            foreach (var e in enterprises.OrderBy(e => e.DisplayName ?? e.Slug))
            {
                var mine = rows.Where(r => r.EnterpriseId == e.Id).ToList();

                // NULL GrossQuantity means "not captured" (rows written before the column existed), and
                // must never be read as zero — that would render a busy enterprise as an untouched pool.
                var captured = mine.Where(r => r.GrossQuantity is not null).Select(r => r.GrossQuantity!.Value).ToList();
                decimal? consumed = mine.Count > 0 && captured.Count == 0 ? null : captured.Sum();
                var incomplete = mine.Count > 0 && captured.Count > 0 && captured.Count < mine.Count;

                // Capacity from ASSIGNED SEATS, per plan. A plan the map does not price contributes
                // nothing to the sum but IS carried in the breakdown, so the card can say "N seats on
                // plan X are not counted" instead of quietly returning a capacity that is too small.
                var plans = seatRows
                    .Where(s => s.EnterpriseId == e.Id)
                    .OrderByDescending(s => s.Seats).ThenBy(s => s.PlanType)
                    .Select(s => new SeatPlan(
                        s.PlanType,
                        s.Seats,
                        creditsPerSeatByPlan.TryGetValue(s.PlanType, out var rate) ? rate : null))
                    .ToList();

                var pricedCredits = plans.Where(p => p.Credits is not null).Sum(p => p.Credits!.Value);
                decimal? capacity = pricedCredits > 0 ? pricedCredits : null;

                decimal? runRate = consumed is decimal c && daysElapsed > 0 ? c / daysElapsed : null;
                decimal? projected = runRate is decimal r ? r * daysInMonth : null;
                int? exhaustionDay = null;
                if (capacity is decimal cap && runRate is decimal rate && rate > 0)
                {
                    var day = (int)Math.Ceiling(cap / rate);
                    // Only meaningful if it lands inside this month; beyond that the pool simply holds.
                    if (day <= daysInMonth) exhaustionDay = Math.Max(day, daysElapsed);
                }

                pools.Add(new AllowancePool(
                    e.Id, string.IsNullOrWhiteSpace(e.DisplayName) ? e.Slug : e.DisplayName!,
                    plans, capacity, consumed, incomplete,
                    daysElapsed, daysInMonth, runRate, projected, exhaustionDay));
            }
            return pools;
        }

        /// <summary>One day of the month-to-date burn, in credits. <see cref="CumulativeCredits"/> is
        /// the running total AS OF that day, not that day's own consumption.</summary>
        public sealed record BurnDownPoint(int Day, decimal CumulativeCredits);

        /// <summary>
        /// The allowance burn-down for one enterprise's current month: cumulative credits consumed,
        /// per day.
        ///
        /// <see cref="DailyUsageSnapshot"/> rows are ALREADY CUMULATIVE month-to-date, so this is a
        /// per-day SUM ACROSS USERS of a value that is itself a running total — there is no
        /// differencing to do, and none must be introduced. Summing across DAYS, by contrast, is the
        /// ~30x inflation the table's own comment warns about, and is exactly what a naive
        /// GROUP BY-less aggregation would produce.
        ///
        /// Reads <c>GrossQuantity</c>, not <c>NetQuantity</c>: net is post-discount and sits at zero
        /// for any month the allowance fully covers, which is precisely the month a burn-down is
        /// wanted for. Days whose rows predate that column are skipped rather than counted as zero,
        /// so a partially-captured month shows a shorter curve instead of a false dip to nothing.
        /// </summary>
        public async Task<IReadOnlyList<BurnDownPoint>> GetAllowanceBurnDownAsync(
            long enterpriseId, int year, int month, UserScope scope, CancellationToken ct = default)
        {
            var all = await GetAllowanceBurnDownsAsync(new[] { enterpriseId }, year, month, scope, ct);
            return all.TryGetValue(enterpriseId, out var pts) ? pts : Array.Empty<BurnDownPoint>();
        }

        /// <summary>
        /// The same curve for SEVERAL enterprises in ONE query — what the all-enterprises overview
        /// needs for its sparklines. A reader granted ten enterprises would otherwise fire ten
        /// round trips to render one screen.
        /// </summary>
        public async Task<IReadOnlyDictionary<long, IReadOnlyList<BurnDownPoint>>> GetAllowanceBurnDownsAsync(
            IEnumerable<long> enterpriseIds, int year, int month, UserScope scope, CancellationToken ct = default)
        {
            var ids = enterpriseIds.Where(scope.CanReadEnterprise).Distinct().ToList();
            var empty = (IReadOnlyDictionary<long, IReadOnlyList<BurnDownPoint>>)
                new Dictionary<long, IReadOnlyList<BurnDownPoint>>();
            if (ids.Count == 0) return empty;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.DailyUsageSnapshots
                .Where(x => ids.Contains(x.EnterpriseId) && x.Year == year && x.Month == month)
                .Where(x => x.UnitType == UsageUnitTypes.AiCredits && x.GrossQuantity != null)
                .Select(x => new { x.EnterpriseId, x.Day, Qty = x.GrossQuantity!.Value })
                .ToListAsync(ct);

            return rows
                .GroupBy(r => r.EnterpriseId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<BurnDownPoint>)g
                        .GroupBy(r => r.Day)
                        .Select(d => new BurnDownPoint(d.Key, d.Sum(r => r.Qty)))
                        .OrderBy(p => p.Day)
                        .ToList());
        }

        /// <summary>A cost center's share of one enterprise's allowance burn.</summary>
        public sealed record PoolContributor(string? CostCenterId, string? CostCenterName, decimal Credits);

        /// <summary>
        /// Cost centers ranked by credits consumed against one enterprise's pool this month.
        ///
        /// An OUTLIER view, not a bill. Credits are pooled across the enterprise, so a cost center
        /// above its notional share costs nothing at all while the pool holds — this answers "who is
        /// driving the burn", never "who owes what". The UI must say so.
        ///
        /// Enterprise-grain gated like the rest of the pool: a cost-center manager gets nothing,
        /// because seeing the ranking means seeing every other team's consumption.
        /// </summary>
        public async Task<IReadOnlyList<PoolContributor>> GetPoolContributorsAsync(
            long enterpriseId, int year, int month, UserScope scope, int top = 8, CancellationToken ct = default)
        {
            if (!scope.CanReadEnterprise(enterpriseId)) return Array.Empty<PoolContributor>();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.UsageSnapshots
                .Where(x => x.EnterpriseId == enterpriseId && x.Year == year && x.Month == month)
                .Where(x => x.UnitType == UsageUnitTypes.AiCredits && x.GrossQuantity != null)
                .Select(x => new { x.CostCenterId, x.CostCenterName, Qty = x.GrossQuantity!.Value })
                .ToListAsync(ct);

            var ranked = rows
                .GroupBy(r => new { r.CostCenterId, r.CostCenterName })
                .Select(g => new PoolContributor(g.Key.CostCenterId, g.Key.CostCenterName, g.Sum(r => r.Qty)))
                .OrderByDescending(c => c.Credits)
                .ToList();

            // Rows with no cost center are enterprise-level consumption, not an error. Folding them
            // into an "Unattributed" row keeps the ranking reconciling against the pool total;
            // dropping them would make the parts silently fail to sum to the whole.
            if (ranked.Count <= top) return ranked;
            var head = ranked.Take(top).ToList();
            var rest = ranked.Skip(top).Sum(c => c.Credits);
            if (rest > 0) head.Add(new PoolContributor(null, "Other cost centers", rest));
            return head;
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
        /// <summary>
        /// <see cref="Organization"/> is sourced from a DIFFERENT table (<see cref="OrgUsageSnapshot"/>)
        /// than every other dimension, because GitHub's per-user usage report carries no organization
        /// at all — its top-level org field merely echoes a filter you passed.
        /// </summary>
        public enum SeriesDimension { Total, User, Model, CostCenter, Enterprise, Organization }
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

        /// <summary>
        /// Turns CUMULATIVE month-to-date readings into per-day spend by differencing consecutive
        /// observations within each (enterprise, user, model, sku, month) series, and projects the
        /// result onto <see cref="UsageSnapshot"/> so the existing bucketing, keying and labelling
        /// all apply unchanged.
        ///
        /// Behaviours worth knowing, because each one is a real situation rather than a hypothetical:
        ///  * FIRST observation of a month carries everything accrued up to that day. If the first
        ///    run of the month is on the 3rd, that row holds days 1-3 and is attributed to the 3rd —
        ///    there is no information available to split it further.
        ///  * MISSED RUNS produce a difference spanning the gap, attributed to the day the reading
        ///    resumed. Honest, and preferable to inventing a distribution across days we never saw.
        ///  * NEGATIVE differences are preserved, not clamped. GitHub restating a figure downward is
        ///    real, and hiding the correction would leave the daily series disagreeing with the
        ///    monthly total for no visible reason.
        /// </summary>
        public static List<UsageSnapshot> ToPerDayRows(IEnumerable<DailyUsageSnapshot> cumulative)
        {
            var result = new List<UsageSnapshot>();
            foreach (var series in cumulative.GroupBy(r =>
                         (r.EnterpriseId, r.Year, r.Month, r.UserLogin, r.Model, r.Sku)))
            {
                decimal prevNet = 0m, prevGross = 0m, prevQty = 0m;
                var first = true;
                foreach (var r in series.OrderBy(x => x.Day))
                {
                    // First reading of the month is itself the delta — there is no earlier baseline.
                    var netDelta = first ? r.NetAmount : r.NetAmount - prevNet;
                    var grossDelta = first ? r.GrossAmount : r.GrossAmount - prevGross;
                    var qtyDelta = first ? r.NetQuantity : r.NetQuantity - prevQty;

                    result.Add(new UsageSnapshot
                    {
                        EnterpriseId = r.EnterpriseId,
                        SnapshotUtc = r.SnapshotUtc,
                        Year = r.Year, Month = r.Month, Day = r.Day,
                        UserLogin = r.UserLogin, UserName = r.UserName,
                        CostCenterId = r.CostCenterId, CostCenterName = r.CostCenterName,
                        Product = r.Product, Sku = r.Sku, Model = r.Model,
                        NetQuantity = qtyDelta, NetAmount = netDelta, GrossAmount = grossDelta,
                    });

                    prevNet = r.NetAmount; prevGross = r.GrossAmount; prevQty = r.NetQuantity;
                    first = false;
                }
            }
            return result;
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

            // ── Organization: different table, and ENTERPRISE-GRAIN ONLY ──
            // OrgUsageSnapshot carries EnterpriseId but NO cost center and NO user, so the scope
            // filter cannot narrow it below the enterprise. Showing it to a cost-center-scoped
            // manager would therefore expose every OTHER team's spend in that enterprise — the exact
            // leak the access model exists to prevent. There is no partial version of this: without
            // a cost-center column there is nothing to filter on.
            //
            // Enterprise Reader is exactly that grain, so it qualifies where a manager does not — but
            // a reader for one enterprise must not see another's organizations, which is why
            // BuildOrgSeriesAsync restricts the table rather than relying on this gate alone.
            if (dim == SeriesDimension.Organization)
            {
                if (!scope.HasEnterpriseRead) return Array.Empty<Series>();
                return await BuildOrgSeriesAsync(db, gran, count, scope, topN, ct);
            }

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

            // ── Intra-month detail ──
            // UsageSnapshot holds ONE row per month (Day = 1) for real data, so day/week buckets
            // would collapse to a single point per month. DailyUsageSnapshot holds the cumulative
            // readings that make real per-day figures possible; difference them and substitute.
            //
            // Substitution is PER MONTH, not wholesale: months predating this feature have no daily
            // rows and must keep rendering from their monthly total rather than vanishing from the
            // chart. It also keeps the mock's fabricated per-day history working in demo mode.
            if (gran is TimeGranularity.Day or TimeGranularity.Week)
            {
                var dq = ApplyScope(db.DailyUsageSnapshots, scope);
                if (!string.IsNullOrWhiteSpace(filterUser)) dq = dq.Where(x => x.UserLogin == filterUser);
                if (!string.IsNullOrWhiteSpace(filterModel)) dq = dq.Where(x => x.Model == filterModel);
                if (!string.IsNullOrWhiteSpace(filterCostCenter))
                {
                    var (dEnt, dCc) = ParseCostCenterKey(filterCostCenter);
                    dq = dEnt is long de
                        ? dq.Where(x => x.EnterpriseId == de && x.CostCenterId == dCc)
                        : dq.Where(x => x.CostCenterId == dCc);
                }
                var perDay = ToPerDayRows(await dq.ToListAsync(ct));
                if (perDay.Count > 0)
                {
                    var covered = perDay.Select(r => (r.EnterpriseId, r.Year, r.Month)).ToHashSet();
                    rows = rows.Where(r => !covered.Contains((r.EnterpriseId, r.Year, r.Month)))
                               .Concat(perDay)
                               .ToList();
                }
            }

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

        /// <summary>
        /// Series grouped by GitHub ORGANIZATION, built from <see cref="OrgUsageSnapshot"/>.
        ///
        /// Callers must have already established that the viewer can see everything — see the gate
        /// in <see cref="GetSeriesAsync"/>; this method does not re-check.
        ///
        /// Two differences from the per-user path are worth knowing:
        ///  * Org rows are TRUE PER-DAY values, so they are summed directly. No differencing, unlike
        ///    <see cref="DailyUsageSnapshot"/> whose rows are cumulative.
        ///  * Rows with no organization are REAL SPEND (enterprise-level charges — a live sample had
        ///    15 of 37) and are surfaced as "Unattributed" rather than dropped, so the series still
        ///    reconciles to the enterprise total.
        /// </summary>
        private async Task<IReadOnlyList<Series>> BuildOrgSeriesAsync(
            BillingDbContext db, TimeGranularity gran, int count, UserScope scope, int topN, CancellationToken ct)
        {
            const string Unattributed = "Unattributed";

            var oq = db.OrgUsageSnapshots.AsQueryable();

            // Restrict to the enterprises this viewer may read AT ALL, intersected with the UI's
            // enterprise filter. Null means "no restriction" (a global reader with no filter); an
            // EMPTY list means nothing may be shown — the two must not be conflated, or an
            // Enterprise Reader whose filter selects an enterprise they lack would see everything.
            var allowedEnterprises = scope.EnterpriseReadFilter();
            if (allowedEnterprises is not null)
            {
                if (allowedEnterprises.Count == 0) return Array.Empty<Series>();
                var allowedIds = allowedEnterprises.ToList();
                oq = oq.Where(x => allowedIds.Contains(x.EnterpriseId));
            }

            var orgRows = await oq.ToListAsync(ct);
            if (orgRows.Count == 0) return Array.Empty<Series>();

            // Project onto UsageSnapshot so the shared bucketing/labelling applies unchanged.
            var rows = orgRows.Select(r => new UsageSnapshot
            {
                EnterpriseId = r.EnterpriseId,
                Year = r.Year, Month = r.Month, Day = r.Day,
                NetAmount = r.NetAmount, GrossAmount = r.GrossAmount,
                OrganizationName = string.IsNullOrWhiteSpace(r.OrganizationName) ? Unattributed : r.OrganizationName,
            }).ToList();

            var bucketed = rows.Select(r => (Bucket: BucketOf(r, gran), Row: r)).ToList();
            var allBuckets = bucketed.Select(b => b.Bucket).Distinct().OrderBy(b => b).ToList();
            if (count > 0 && allBuckets.Count > count) allBuckets = allBuckets.Skip(allBuckets.Count - count).ToList();
            var bucketSet = allBuckets.ToHashSet();
            var win = bucketed.Where(b => bucketSet.Contains(b.Bucket)).ToList();

            Series Build(string key, IEnumerable<(DateOnly Bucket, UsageSnapshot Row)> items)
            {
                var byBucket = items.GroupBy(i => i.Bucket).ToDictionary(g => g.Key, g => g.Sum(v => v.Row.NetAmount));
                var pts = allBuckets.Select(b => new SeriesPoint(b, LabelOf(b, gran), byBucket.TryGetValue(b, out var v) ? v : 0m)).ToList();
                return new Series(key, pts, pts.Sum(p => p.NetAmount));
            }

            var series = win.GroupBy(b => b.Row.OrganizationName ?? Unattributed)
                .Select(g => Build(g.Key, g))
                .OrderByDescending(s => s.Total)
                .ToList();

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
