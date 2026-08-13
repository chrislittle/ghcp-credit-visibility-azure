using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using GhcpCreditVisibility.Data;

namespace GhcpCreditVisibility.Services
{
    /// <summary>
    /// Pulls current-month usage for every licensed user of every ENABLED registry enterprise from
    /// that enterprise's billing client (real or mock, routed per row) and upserts it into the
    /// database, then purges snapshots older than the retention window. Enterprises run
    /// SEQUENTIALLY with per-enterprise error isolation: one enterprise's expired PAT or API outage
    /// records a failed <see cref="SnapshotRun"/> for THAT enterprise and the loop continues — it
    /// never aborts the others. This is the ONLY caller of the GitHub API — the UI reads exclusively
    /// from the database. The registry is re-read every cycle, so a newly enabled enterprise is
    /// picked up on the next run with no restart.
    /// </summary>
    public sealed class SnapshotService
    {
        private readonly IEnterpriseBillingClientFactory _clientFactory;
        private readonly EnterpriseRegistryService _registry;
        private readonly IDbContextFactory<BillingDbContext> _dbFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SnapshotService> _logger;
        private readonly GitHubRateLimitRegistry _rateLimits;
        private readonly TelemetryClient? _telemetry;

        public SnapshotService(
            IEnterpriseBillingClientFactory clientFactory,
            EnterpriseRegistryService registry,
            IDbContextFactory<BillingDbContext> dbFactory,
            IConfiguration config,
            ILogger<SnapshotService> logger,
            GitHubRateLimitRegistry rateLimits,
            TelemetryClient? telemetry = null)
        {
            _clientFactory = clientFactory;
            _registry = registry;
            _dbFactory = dbFactory;
            _config = config;
            _logger = logger;
            _rateLimits = rateLimits;
            _telemetry = telemetry;
        }

        /// <summary>App Service instance running this snapshot — lets the SnapshotRunCompleted event distinguish concurrent runs.</summary>
        private static string InstanceId =>
            Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "local";

        /// <summary>
        /// Floor on months of history kept, regardless of configuration. Reports and trends need at
        /// least a quarter to mean anything.
        ///
        /// CORRECTED 2026-08-11 (verified against the live API): purged rows are NOT permanently
        /// lost. GitHub's billing usage endpoints take optional year/month/day parameters and serve
        /// a rolling TWO-YEAR window — a request beyond it fails with "Time period cannot be more
        /// than 2 years in the past." Earlier comments here claimed the API served the current month
        /// only; that was wrong, and it made every retention decision look more final than it is.
        ///
        /// The floor still earns its place. Purged months CAN now be re-fetched — organization data
        /// automatically, per-user data via the opt-in backfill below — but per-user recovery costs a
        /// request per user per month, and anything older than 24 months is gone for good regardless.
        /// A short retention window therefore still trades cheap storage for expensive (or impossible)
        /// recovery.
        /// </summary>
        public const int MinRetentionMonths = 3;

        /// <summary>
        /// Returns the first (Year, Month) that is KEPT; rows strictly older than it are purged.
        /// Expressed as integers rather than a <see cref="DateTime"/> because the purge predicate
        /// compares the Year/Month columns directly — building a DateTime from column values inside
        /// the query is not translatable by the SQL Server provider.
        /// </summary>
        public static (int Year, int Month) ComputeRetentionCutoff(DateTime nowUtc, int retentionMonths)
        {
            var months = Math.Max(MinRetentionMonths, retentionMonths);
            var cutoff = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-months);
            return (cutoff.Year, cutoff.Month);
        }

        /// <summary>
        /// Months of DAILY history to keep. Defaults to the monthly window: a deployment where the
        /// monthly trend reaches further back than the daily detail, for reasons nobody remembers, is
        /// a worse default than simply keeping both the same. Daily rows are ~30x more numerous but
        /// still small in absolute terms (single-digit GB at enterprise scale, cents per month on
        /// Azure SQL), so the separate knob exists for very large deployments rather than as the
        /// expected thing to tune. The same <see cref="MinRetentionMonths"/> floor applies, and here
        /// it is stricter than it looks: daily rows are the one thing backfill CANNOT restore. It
        /// fetches a past month as a single figure, so purged intra-month detail is gone even though
        /// the month's total comes back.
        /// </summary>
        public static int ResolveDailyRetentionMonths(int monthlyRetention, int? dailyRetentionOverride) =>
            Math.Max(MinRetentionMonths, dailyRetentionOverride ?? monthlyRetention);

        /// <summary>Months of organization history fetched per snapshot cycle. Kept small so a new
        /// enterprise catches up over a few runs instead of firing a burst of calls on its first.</summary>
        public const int OrgBackfillMonthsPerRun = 3;

        /// <summary>
        /// GitHub's hard limit: "Time period cannot be more than 2 years in the past." Requesting
        /// beyond it returns HTTP 400, so the plan never does.
        /// </summary>
        public const int GitHubHistoryMonths = 24;

        /// <summary>
        /// The month AI credits became GitHub Copilot's billing unit, replacing premium requests.
        ///
        /// Nothing before this can have AI-credit usage — the meter did not exist — so requesting
        /// earlier periods spends API calls to receive empty responses, and any pre-epoch figure
        /// would be denominated in premium requests, a different unit that must never sit alongside
        /// credits in the same chart. This is a fact about GitHub's billing, not a preference, which
        /// is why it is a constant rather than configuration.
        ///
        /// It is currently the BINDING bound on backfill depth (tighter than either retention or
        /// GitHub's 24-month window); retention will usually become the binding one from mid-2027.
        /// </summary>
        public static readonly DateTime AiCreditEpoch = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Whole months of PER-USER history still to fetch, newest first.
        ///
        /// Whole months, deliberately: at any instant the stored data is then "complete from month X
        /// to now, absent before" — a clean boundary the "collecting since" marker states truthfully.
        /// Filling user-by-user across months instead would leave some users with history and others
        /// without, which is invisible in a chart and indistinguishable from those people not having
        /// used Copilot.
        ///
        /// The floor is the MOST RECENT of three bounds, because each is a hard reason the data
        /// cannot exist or cannot be kept:
        ///   * the AI-credits epoch — the meter did not exist earlier
        ///   * GitHub's 24-month window — the API refuses beyond it
        ///   * the retention window — the purge would delete it on the same run
        /// </summary>
        public static IReadOnlyList<(int Year, int Month)> PlanUserBackfill(
            DateTime nowUtc, int retentionMonths, (int Year, int Month)? oldestDone)
        {
            var current = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            var retentionFloor = current.AddMonths(-Math.Max(MinRetentionMonths, retentionMonths));
            var apiFloor = current.AddMonths(-GitHubHistoryMonths);
            var floor = new[] { retentionFloor, apiFloor, AiCreditEpoch }.Max();

            // Resume one month older than the watermark; with none, start at last month. The CURRENT
            // month is never included — the normal snapshot path owns it.
            var next = oldestDone is { } w
                ? new DateTime(w.Year, w.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1)
                : current.AddMonths(-1);

            var plan = new List<(int, int)>();
            while (next >= floor)
            {
                plan.Add((next.Year, next.Month));
                next = next.AddMonths(-1);
            }
            return plan;
        }

        /// <summary>
        /// Estimated API calls to fill the outstanding months: one call per user per month. Used to
        /// show an operator the cost BEFORE they commit — 14 calls and 60,000 calls are the same
        /// button otherwise.
        /// </summary>
        public static int EstimateUserBackfillCalls(int licensedUsers, int monthsOutstanding) =>
            Math.Max(0, licensedUsers) * Math.Max(0, monthsOutstanding);

        /// <summary>
        /// Which past months to fetch organization usage for on this cycle.
        ///
        /// Walks BACKWARDS from the month before <paramref name="nowUtc"/>, resuming from
        /// <paramref name="oldestDone"/> (the watermark) and stopping at the retention floor —
        /// because anything older is deleted by the purge on this very same run, so fetching it
        /// would be pure waste repeated every cycle. That coupling is the reason retention and
        /// backfill depth cannot be reasoned about separately: to hold two years of organization
        /// history you must first RAISE retention; widening the backfill alone achieves nothing.
        ///
        /// Returns an empty list once the watermark reaches the floor, and never returns the
        /// current month (the normal snapshot path owns that one).
        /// </summary>
        public static IReadOnlyList<(int Year, int Month)> PlanOrgBackfill(
            DateTime nowUtc, int retentionMonths, (int Year, int Month)? oldestDone, int maxMonths = OrgBackfillMonthsPerRun)
        {
            var current = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

            // Oldest month worth holding: the retention floor, never beyond GitHub's 24-month window.
            var floor = current.AddMonths(-Math.Min(Math.Max(MinRetentionMonths, retentionMonths), GitHubHistoryMonths));

            // Resume one month older than the watermark; with none, start at last month.
            var next = oldestDone is { } w
                ? new DateTime(w.Year, w.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1)
                : current.AddMonths(-1);

            var plan = new List<(int, int)>();
            while (plan.Count < maxMonths && next >= floor)
            {
                plan.Add((next.Year, next.Month));
                next = next.AddMonths(-1);
            }
            return plan;
        }

        /// <summary>
        /// One full cycle: bootstrap the registry (idempotent), then snapshot each enabled
        /// enterprise in turn. Per-enterprise failures are recorded and swallowed (isolation);
        /// only infrastructure failures (registry/DB unreachable) propagate to the caller's
        /// retry/backoff.
        /// </summary>
        public async Task RunAsync(CancellationToken ct = default)
        {
            await _registry.EnsureBootstrapAsync(ct);
            var enterprises = await _registry.GetEnabledAsync(ct);
            if (enterprises.Count == 0)
            {
                _logger.LogWarning("Snapshot cycle skipped: no enabled enterprises in the registry.");
                return;
            }

            foreach (var enterprise in enterprises)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RunForEnterpriseAsync(enterprise, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Isolation by design: this enterprise's run row + SnapshotFailed event already
                    // record the failure (see RunForEnterpriseAsync); the remaining enterprises
                    // still get their snapshot this cycle.
                    _logger.LogError(ex, "Snapshot for enterprise '{Slug}' failed; continuing with the next enterprise.", enterprise.Slug);
                }
            }
        }

        /// <summary>
        /// Snapshots ONE enterprise by id. Used by the admin console's "Run now" — the scheduled
        /// loop calls <see cref="RunForEnterpriseAsync"/> directly for each enabled enterprise.
        ///
        /// Callers are responsible for holding the snapshot lease (see
        /// <see cref="ManualSnapshotTrigger"/>); this method does not take it, so that the
        /// scheduled loop is not forced to re-acquire per enterprise mid-cycle.
        /// </summary>
        public async Task RunOneAsync(long enterpriseId, CancellationToken ct = default)
        {
            await _registry.EnsureBootstrapAsync(ct);
            var enterprise = (await _registry.GetAllAsync(ct)).FirstOrDefault(e => e.Id == enterpriseId)
                ?? throw new InvalidOperationException($"Enterprise {enterpriseId} is not in the registry.");

            // Disabled enterprises are excluded from the scheduled loop; running one by hand would
            // quietly reintroduce data the operator deliberately stopped collecting.
            if (!enterprise.Enabled)
                throw new InvalidOperationException($"Enterprise '{enterprise.Slug}' is disabled — enable it first.");

            await RunForEnterpriseAsync(enterprise, ct);
        }

        private async Task RunForEnterpriseAsync(Enterprise enterprise, CancellationToken ct)
        {
            var retentionMonths = _config.GetValue("Retention:Months", 6);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var run = new SnapshotRun { EnterpriseId = enterprise.Id, StartedUtc = DateTime.UtcNow };
            db.SnapshotRuns.Add(run);
            await db.SaveChangesAsync(ct);

            try
            {
                var now = DateTime.UtcNow;
                var client = await _clientFactory.GetClientAsync(enterprise, ct);
                var users = await client.GetEnterpriseUsersAsync(enterprise.Slug, ct);
                var costCenters = await client.GetCostCentersAsync(enterprise.Slug, ct);
                var userToCc = BuildUserCostCenterMap(costCenters);

                // Remembered so the admin console can estimate backfill cost (users x months) without
                // calling GitHub — pages in this app never do.
                //
                // NOTE what this is NOT: a Copilot seat count. These are GHEC LICENCE holders, and a
                // person can hold a licence with no Copilot seat (a live enterprise showed 8 licences
                // against 3 seats). Anything sizing the included-credit allowance must use the seat
                // counts collected below instead — see EnterpriseCopilotSeat.
                await _registry.SetLicensedUserCountAsync(enterprise.Id, users.Count, ct);

                // ── Copilot seats, per plan ──────────────────────────────────────────────────────
                // One call per enterprise per run (paginated past 100 seats). Grouped by plan because
                // included credits differ — Business 1,900 per seat, Enterprise 3,900 — so a mixed
                // enterprise's capacity is a sum over plans.
                //
                // Non-fatal, exactly like the organization block further down: an enterprise not on
                // the endpoint, or a PAT without the scope, must not fail a run whose per-user
                // numbers are the app's primary output. On failure the PREVIOUS seat rows are left
                // untouched (the delete lives inside the try), so the pool keeps rendering from the
                // last known good counts rather than blanking on one bad call.
                try
                {
                    var seats = await client.GetCopilotSeatsAsync(enterprise.Slug, ct);
                    var byPlan = seats
                        .GroupBy(s => string.IsNullOrWhiteSpace(s.PlanType)
                            ? "unknown"
                            : s.PlanType!.Trim().ToLowerInvariant())
                        .Select(g => new { Plan = g.Key, Count = g.Count() })
                        .ToList();

                    // Replace-per-enterprise-per-MONTH rather than upsert: one response IS the whole
                    // current seat list, so a plan that disappears must vanish from this month's rows
                    // rather than linger — while earlier months stay untouched, because they are the
                    // history GitHub cannot re-serve.
                    var existingSeatRows = db.EnterpriseCopilotSeats
                        .Where(x => x.EnterpriseId == enterprise.Id && x.Year == now.Year && x.Month == now.Month);
                    if (db.Database.IsRelational())
                    {
                        await existingSeatRows.ExecuteDeleteAsync(ct);
                    }
                    else
                    {
                        db.EnterpriseCopilotSeats.RemoveRange(await existingSeatRows.ToListAsync(ct));
                        await db.SaveChangesAsync(ct);
                    }

                    foreach (var p in byPlan)
                    {
                        db.EnterpriseCopilotSeats.Add(new EnterpriseCopilotSeat
                        {
                            EnterpriseId = enterprise.Id,
                            Year = now.Year,
                            Month = now.Month,
                            PlanType = p.Plan,
                            Seats = p.Count,
                            SnapshotUtc = now
                        });
                    }
                    await db.SaveChangesAsync(ct);
                    _logger.LogInformation("Copilot seats for '{Slug}': {Total} across {Plans} plan(s).",
                        enterprise.Slug, seats.Count, byPlan.Count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Non-fatal must not mean invisible — same event shape as OrgUsageUnavailable so
                    // one alert rule can cover both.
                    _logger.LogWarning(ex, "Copilot seat counts unavailable for '{Slug}'; usage data is unaffected.", enterprise.Slug);
                    _telemetry?.TrackEvent("CopilotSeatsUnavailable",
                        new Dictionary<string, string>
                        {
                            ["error"] = ex.Message,
                            ["instanceId"] = InstanceId,
                            ["enterprise"] = enterprise.Slug,
                        });
                }

                var written = 0;
                foreach (var u in users)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(u.GitHubComLogin)) continue;

                    var usage = await client.GetUsageForUserAsync(enterprise.Slug, u.GitHubComLogin, now.Year, now.Month, ct);
                    if (usage?.UsageItems is null) continue;

                    var (ccId, ccName) = usage.CostCenter is not null
                        ? (usage.CostCenter.Id, usage.CostCenter.Name)
                        : userToCc.GetValueOrDefault(u.GitHubComLogin);

                    foreach (var item in usage.UsageItems)
                    {
                        var existing = await db.UsageSnapshots.FirstOrDefaultAsync(x =>
                            x.EnterpriseId == enterprise.Id &&
                            x.Year == now.Year && x.Month == now.Month && x.Day == 1 &&
                            x.UserLogin == u.GitHubComLogin && x.Model == item.Model && x.Sku == item.Sku, ct);

                        if (existing is null)
                        {
                            db.UsageSnapshots.Add(new UsageSnapshot
                            {
                                EnterpriseId = enterprise.Id,
                                SnapshotUtc = now, Year = now.Year, Month = now.Month, Day = 1,
                                UserLogin = u.GitHubComLogin, UserName = u.GitHubComName,
                                CostCenterId = ccId, CostCenterName = ccName,
                                Product = item.Product, Sku = item.Sku, Model = item.Model, UnitType = item.UnitType,
                                NetQuantity = item.NetQuantity, NetAmount = item.NetAmount, GrossAmount = item.GrossAmount,
                                DiscountQuantity = item.DiscountQuantity, DiscountAmount = item.DiscountAmount,
                                PricePerUnit = item.PricePerUnit, GrossQuantity = item.GrossQuantity
                            });
                        }
                        else
                        {
                            existing.SnapshotUtc = now;
                            existing.CostCenterId = ccId; existing.CostCenterName = ccName;
                            existing.NetQuantity = item.NetQuantity; existing.NetAmount = item.NetAmount; existing.GrossAmount = item.GrossAmount;
                            // Must be updated alongside the amounts above: within a month this row is
                            // rewritten in place on every run, so a field refreshed on INSERT but not
                            // on UPDATE would silently freeze at its first-of-month value.
                            existing.DiscountQuantity = item.DiscountQuantity; existing.DiscountAmount = item.DiscountAmount;
                            existing.PricePerUnit = item.PricePerUnit; existing.GrossQuantity = item.GrossQuantity;
                        }

                        // ── Intra-month history ──
                        // The row above is rewritten in place all month, so by month end it knows the
                        // TOTAL but not how the month got there. This records today's cumulative
                        // reading as its own row so "which day did it jump?" stays answerable.
                        // Cumulative, not a delta, and keyed on the day — so re-running today
                        // overwrites rather than double-counts. See DailyUsageSnapshot.
                        var daily = await db.DailyUsageSnapshots.FirstOrDefaultAsync(x =>
                            x.EnterpriseId == enterprise.Id &&
                            x.Year == now.Year && x.Month == now.Month && x.Day == now.Day &&
                            x.UserLogin == u.GitHubComLogin && x.Model == item.Model && x.Sku == item.Sku, ct);

                        if (daily is null)
                        {
                            db.DailyUsageSnapshots.Add(new DailyUsageSnapshot
                            {
                                EnterpriseId = enterprise.Id,
                                SnapshotUtc = now, Year = now.Year, Month = now.Month, Day = now.Day,
                                UserLogin = u.GitHubComLogin, UserName = u.GitHubComName,
                                CostCenterId = ccId, CostCenterName = ccName,
                                Product = item.Product, Sku = item.Sku, Model = item.Model, UnitType = item.UnitType,
                                // GrossQuantity is what the allowance burn-down is drawn from —
                                // NetQuantity is post-discount and sits at ZERO for any month the
                                // allowance covers, which is precisely the month you want the curve for.
                                NetQuantity = item.NetQuantity, GrossQuantity = item.GrossQuantity,
                                NetAmount = item.NetAmount, GrossAmount = item.GrossAmount
                            });
                        }
                        else
                        {
                            daily.SnapshotUtc = now;
                            daily.CostCenterId = ccId; daily.CostCenterName = ccName;
                            daily.NetQuantity = item.NetQuantity; daily.GrossQuantity = item.GrossQuantity;
                            daily.NetAmount = item.NetAmount; daily.GrossAmount = item.GrossAmount;
                        }
                        written++;
                    }
                    await db.SaveChangesAsync(ct);
                }

                // ── Organization / repository attribution (ONE call for the whole month) ──
                // The per-user AI-credit report carries no organization: its top-level `user` and
                // `organization` fields merely ECHO the filters you passed, so org attribution can
                // never come from that loop. This endpoint supplies organizationName,
                // repositoryName and a per-item date in a single request — no per-user fan-out, one
                // call against the rate-limit budget. It has no user attribution, so the two
                // sources are complementary and neither replaces the other.
                //
                // Replace-per-month rather than upsert: one response IS the whole month, and the
                // natural key would need nullable org/repo columns (see the entity config).
                try
                {
                    var orgItems = await client.GetOrgUsageAsync(enterprise.Slug, now.Year, now.Month, ct);
                    var existingOrgRows = db.OrgUsageSnapshots
                        .Where(x => x.EnterpriseId == enterprise.Id && x.Year == now.Year && x.Month == now.Month);
                    if (db.Database.IsRelational())
                    {
                        await existingOrgRows.ExecuteDeleteAsync(ct);
                    }
                    else
                    {
                        db.OrgUsageSnapshots.RemoveRange(await existingOrgRows.ToListAsync(ct));
                        await db.SaveChangesAsync(ct);
                    }

                    foreach (var oi in orgItems)
                    {
                        // Date comes from the LINE ITEM, not the run clock — that is what makes this
                        // table genuinely daily. Items without one fall back to the 1st rather than
                        // being dropped.
                        var d = oi.Date ?? new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                        db.OrgUsageSnapshots.Add(new OrgUsageSnapshot
                        {
                            EnterpriseId = enterprise.Id,
                            SnapshotUtc = now,
                            Year = d.Year, Month = d.Month, Day = d.Day,
                            OrganizationName = oi.OrganizationName,   // null = enterprise-level charge; kept, never dropped
                            RepositoryName = oi.RepositoryName,
                            Product = oi.Product ?? "", Sku = oi.Sku ?? "", UnitType = oi.UnitType,
                            Quantity = oi.Quantity, PricePerUnit = oi.PricePerUnit,
                            GrossAmount = oi.GrossAmount, DiscountAmount = oi.DiscountAmount, NetAmount = oi.NetAmount,
                        });
                    }
                    await db.SaveChangesAsync(ct);

                    // ── Backfill past months, a few per cycle ──
                    // Same endpoint, just older periods — one call covers a whole month, so two
                    // years costs ~24 calls rather than the per-user fan-out that makes user-level
                    // backfill impractical. Bounded by the retention floor (see PlanOrgBackfill).
                    var backfillRetention = ResolveDailyRetentionMonths(
                        retentionMonths, _config.GetValue<int?>("Retention:DailyMonths"));
                    var watermark = enterprise.OrgBackfillOldestYear is int wy && enterprise.OrgBackfillOldestMonth is int wm
                        ? (wy, wm)
                        : ((int, int)?)null;

                    foreach (var (by, bm) in PlanOrgBackfill(now, backfillRetention, watermark))
                    {
                        ct.ThrowIfCancellationRequested();
                        var past = await client.GetOrgUsageAsync(enterprise.Slug, by, bm, ct);

                        // Replace that month wholesale, exactly as the current-month path does, so a
                        // re-run overwrites rather than duplicating.
                        var existingMonth = db.OrgUsageSnapshots
                            .Where(x => x.EnterpriseId == enterprise.Id && x.Year == by && x.Month == bm);
                        if (db.Database.IsRelational()) await existingMonth.ExecuteDeleteAsync(ct);
                        else { db.OrgUsageSnapshots.RemoveRange(await existingMonth.ToListAsync(ct)); await db.SaveChangesAsync(ct); }

                        foreach (var oi in past)
                        {
                            var pd = oi.Date ?? new DateTime(by, bm, 1, 0, 0, 0, DateTimeKind.Utc);
                            db.OrgUsageSnapshots.Add(new OrgUsageSnapshot
                            {
                                EnterpriseId = enterprise.Id,
                                SnapshotUtc = now,
                                Year = pd.Year, Month = pd.Month, Day = pd.Day,
                                OrganizationName = oi.OrganizationName, RepositoryName = oi.RepositoryName,
                                Product = oi.Product ?? "", Sku = oi.Sku ?? "", UnitType = oi.UnitType,
                                Quantity = oi.Quantity, PricePerUnit = oi.PricePerUnit,
                                GrossAmount = oi.GrossAmount, DiscountAmount = oi.DiscountAmount, NetAmount = oi.NetAmount,
                            });
                        }
                        await db.SaveChangesAsync(ct);

                        // Advance the watermark whether or not the month held data — a genuinely
                        // empty month must not be re-fetched on every future cycle.
                        await _registry.SetOrgBackfillWatermarkAsync(enterprise.Id, by, bm, ct);
                        enterprise.OrgBackfillOldestYear = by;
                        enterprise.OrgBackfillOldestMonth = bm;
                        _logger.LogInformation("Backfilled organization usage for '{Slug}' {Year}-{Month:00}: {Count} items.",
                            enterprise.Slug, by, bm, past.Count);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Non-fatal by design: org attribution is supplementary. An enterprise not yet on
                    // the endpoint (404) or a PAT lacking scope must not fail a run whose per-user
                    // numbers — the app's primary output — are already written. A backfill month that
                    // fails simply leaves the watermark where it was and is retried next cycle.
                    _logger.LogWarning(ex, "Organization usage unavailable for '{Slug}'; per-user data is unaffected.", enterprise.Slug);

                    // ...but non-fatal must not mean INVISIBLE. Without this event the run still
                    // reports "succeeded", the app's own logs go to the container's stdout rather
                    // than App Insights, and organization data could stop flowing indefinitely with
                    // nothing to alert on. Same shape as SnapshotFailed so the alert rule can treat
                    // them alike.
                    _telemetry?.TrackEvent("OrgUsageUnavailable",
                        new Dictionary<string, string>
                        {
                            ["error"] = ex.Message,
                            ["instanceId"] = InstanceId,
                            ["enterprise"] = enterprise.Slug,
                        });
                }

                // ── Per-user history backfill (OPT-IN, staged across cycles) ──
                // Runs only when an operator enabled it after seeing the cost estimate. Fills WHOLE
                // months, newest first, so the stored data is always "complete from month X to now"
                // rather than a scatter of half-filled months — the boundary the "collecting since"
                // marker reports, and the reason this walks months rather than users.
                //
                // Bounded per cycle by the live rate-limit reading, not by a fixed count: the regular
                // snapshot above has already run, so whatever remains this hour is genuinely spare.
                // Large enterprises therefore converge over several cycles instead of being refused.
                if (enterprise.UserBackfillEnabled)
                {
                    var backfilled = await BackfillUserMonthsAsync(db, enterprise, client, users, userToCc, now, retentionMonths, ct);
                    written += backfilled;
                }

                // ── Cost-center directory (current names, keyed by (enterprise, GitHub's stable id)) ──
                // Refreshed every run so a rename in GitHub propagates to reports/trends/the admin
                // mapping dropdown without rewriting the frozen historical name on past snapshot rows.
                var existingDirectory = await db.CostCenterDirectory
                    .Where(x => x.EnterpriseId == enterprise.Id)
                    .ToDictionaryAsync(x => x.CostCenterId, ct);
                foreach (var cc in costCenters)
                {
                    if (existingDirectory.TryGetValue(cc.Id, out var dirEntry))
                    {
                        dirEntry.CurrentName = cc.Name;
                        dirEntry.LastSeenUtc = now;
                    }
                    else
                    {
                        var newEntry = new CostCenterDirectoryEntry { EnterpriseId = enterprise.Id, CostCenterId = cc.Id, CurrentName = cc.Name, LastSeenUtc = now };
                        db.CostCenterDirectory.Add(newEntry);
                        existingDirectory[cc.Id] = newEntry;
                    }
                }
                await db.SaveChangesAsync(ct);

                // ── Budgets (GOVERNED IN GITHUB; snapshotted here for read-only display) ──
                // Load THIS enterprise's existing rows once, then track adds made during the run too —
                // querying the DB per iteration misses not-yet-saved Adds, so duplicate scopes/cost-
                // centers in the same batch would otherwise insert twice and violate the unique
                // index on (EnterpriseId, Scope, CostCenterId).
                // Keyed by GitHub's own budget id (see BudgetScopeMapper). The previous key was
                // (Scope, CostCenterId), which collapsed every non-cost-center scope onto one row.
                var existingBudgets = await db.BudgetSnapshots
                    .Where(x => x.EnterpriseId == enterprise.Id)
                    .ToDictionaryAsync(x => x.GitHubBudgetId, StringComparer.Ordinal, ct);
                var seenBudgetKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var gb in await client.GetBudgetsAsync(enterprise.Slug, ct))
                {
                    var key = BudgetScopeMapper.KeyFor(gb);
                    // GitHub returning two budgets with the same id in one response would violate the
                    // unique index and fail the whole run; skip the duplicate and carry on instead.
                    if (!seenBudgetKeys.Add(key))
                    {
                        _logger.LogWarning("Duplicate budget id '{Key}' from GitHub for '{Slug}'; ignoring the repeat.", key, enterprise.Slug);
                        continue;
                    }
                    if (!existingBudgets.TryGetValue(key, out var row))
                    {
                        row = new BudgetSnapshot { EnterpriseId = enterprise.Id };
                        db.BudgetSnapshots.Add(row);
                        existingBudgets[key] = row;
                    }
                    BudgetScopeMapper.Apply(row, gb, costCenters, now);
                }
                // Budgets that GitHub no longer reports for this enterprise are removed — this covers
                // budgets deleted in GitHub AND self-heals rows persisted under a stale key. That
                // self-healing is what migrates the pre-fix rows: on the first run after deploy every
                // row keyed the old way is absent from seenBudgetKeys, so it is deleted and reinserted
                // under its GitHub budget id. No backfill script is required, and the table converges
                // within a single cycle.
                var staleBudgets = existingBudgets
                    .Where(kv => !seenBudgetKeys.Contains(kv.Key))
                    .Select(kv => kv.Value)
                    .ToList();
                if (staleBudgets.Count > 0) db.BudgetSnapshots.RemoveRange(staleBudgets);
                await db.SaveChangesAsync(ct);

                // Retention purge (>= 3 months kept), scoped to THIS enterprise so the purge count on
                // the run row stays meaningful. Comparing Year/Month as integers (rather than
                // constructing a DateTime from column values inside the query) is what SQL Server's
                // EF Core provider can actually translate for ExecuteDelete.
                var (cutoffYear, cutoffMonth) = ComputeRetentionCutoff(now, retentionMonths);
                var stale = db.UsageSnapshots
                    .Where(x => x.EnterpriseId == enterprise.Id)
                    .Where(x => x.Year < cutoffYear || (x.Year == cutoffYear && x.Month < cutoffMonth));

                int purged;
                if (db.Database.IsRelational())
                {
                    // Azure SQL: set-based delete in a single statement, no entities materialized.
                    purged = await stale.ExecuteDeleteAsync(ct);
                }
                else
                {
                    // Local dev (in-memory provider) has no ExecuteDelete support — without this
                    // fallback the whole snapshot run throws here, on its very last step, and the
                    // job never completes locally. Volumes are tiny in dev, so load + RemoveRange
                    // is fine.
                    var staleRows = await stale.ToListAsync(ct);
                    db.UsageSnapshots.RemoveRange(staleRows);
                    await db.SaveChangesAsync(ct);
                    purged = staleRows.Count;
                }

                // Daily rows purge on their own window (defaults to the monthly one).
                var dailyMonths = ResolveDailyRetentionMonths(retentionMonths, _config.GetValue<int?>("Retention:DailyMonths"));
                var (dCutYear, dCutMonth) = ComputeRetentionCutoff(now, dailyMonths);
                var staleDaily = db.DailyUsageSnapshots
                    .Where(x => x.EnterpriseId == enterprise.Id)
                    .Where(x => x.Year < dCutYear || (x.Year == dCutYear && x.Month < dCutMonth));

                if (db.Database.IsRelational())
                {
                    purged += await staleDaily.ExecuteDeleteAsync(ct);
                }
                else
                {
                    var staleDailyRows = await staleDaily.ToListAsync(ct);
                    db.DailyUsageSnapshots.RemoveRange(staleDailyRows);
                    await db.SaveChangesAsync(ct);
                    purged += staleDailyRows.Count;
                }

                // Org usage is day-grained detail like the daily table, so it shares that window.
                var staleOrg = db.OrgUsageSnapshots
                    .Where(x => x.EnterpriseId == enterprise.Id)
                    .Where(x => x.Year < dCutYear || (x.Year == dCutYear && x.Month < dCutMonth));

                if (db.Database.IsRelational())
                {
                    purged += await staleOrg.ExecuteDeleteAsync(ct);
                }
                else
                {
                    var staleOrgRows = await staleOrg.ToListAsync(ct);
                    db.OrgUsageSnapshots.RemoveRange(staleOrgRows);
                    await db.SaveChangesAsync(ct);
                    purged += staleOrgRows.Count;
                }

                run.RowsWritten = written;
                run.RowsPurged = purged;
                run.Status = "succeeded";
                run.CompletedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await _registry.MarkSnapshotCompletedAsync(enterprise.Id, run.CompletedUtc.Value, ct);
                _logger.LogInformation("Snapshot complete for '{Slug}': {Written} rows written, {Purged} purged.", enterprise.Slug, written, purged);

                // Point-in-time signal for the SRE agent / alert rules: WHICH ENTERPRISE, which
                // instance ran, how long it took, and what it wrote. One completion per enterprise
                // per cycle is the healthy pattern (the cross-instance lease still guarantees a
                // single instance runs the whole cycle).
                _telemetry?.TrackEvent("SnapshotRunCompleted",
                    new Dictionary<string, string> { ["status"] = "succeeded", ["instanceId"] = InstanceId, ["enterprise"] = enterprise.Slug },
                    new Dictionary<string, double>
                    {
                        ["rowsWritten"] = written,
                        ["rowsPurged"] = purged,
                        ["durationMs"] = (run.CompletedUtc.Value - run.StartedUtc).TotalMilliseconds,
                    });
            }
            catch (Exception ex)
            {
                run.Status = "failed";
                run.Error = ex.Message;
                run.CompletedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                _logger.LogError(ex, "Snapshot run failed for enterprise '{Slug}'.", enterprise.Slug);

                _telemetry?.TrackEvent("SnapshotFailed",
                    new Dictionary<string, string> { ["error"] = ex.Message, ["instanceId"] = InstanceId, ["enterprise"] = enterprise.Slug },
                    new Dictionary<string, double>
                    {
                        ["durationMs"] = (DateTime.UtcNow - run.StartedUtc).TotalMilliseconds,
                    });
                throw;
            }
        }

        /// <summary>
        /// Reserve of hourly rate limit never spent on backfill, so the regular snapshot always has
        /// room. GitHub allows 5,000 requests/hour per token and a normal cycle uses a few dozen;
        /// this leaves an order of magnitude more than that untouched.
        /// </summary>
        public const int BackfillRateLimitReserve = 500;

        /// <summary>
        /// Fills whole past months of per-user history, newest first, until the plan is exhausted or
        /// the rate limit approaches the reserve. Returns rows written.
        ///
        /// A month is only marked done once EVERY user in it has been fetched, so an interruption
        /// leaves a clean boundary rather than a partially-filled month. Nothing here writes daily
        /// rows: intra-month detail for a past month would cost one call per user PER DAY, and a
        /// single month-end reading rendered as a daily row would attribute the whole month to one
        /// day. Backfilled months therefore appear at month granularity, which the read path already
        /// handles per-month.
        /// </summary>
        private async Task<int> BackfillUserMonthsAsync(
            BillingDbContext db, Enterprise enterprise, IGitHubBillingClient client,
            IReadOnlyList<Models.EnterpriseLicenseUser> users, Dictionary<string, (string?, string?)> userToCc,
            DateTime now, int retentionMonths, CancellationToken ct)
        {
            var watermark = enterprise.UserBackfillOldestYear is int wy && enterprise.UserBackfillOldestMonth is int wm
                ? (wy, wm)
                : ((int, int)?)null;

            var plan = PlanUserBackfill(now, retentionMonths, watermark);
            if (plan.Count == 0)
            {
                // Nothing left — clear the flag so the console shows "complete" and no later cycle
                // re-plans the same finished work.
                await _registry.SetUserBackfillEnabledAsync(enterprise.Id, false, ct);
                _logger.LogInformation("Per-user backfill complete for '{Slug}'.", enterprise.Slug);
                return 0;
            }

            var rate = _rateLimits.For(enterprise.Slug);
            var written = 0;
            var paused = false;

            foreach (var (year, month) in plan)
            {
                ct.ThrowIfCancellationRequested();

                // Stop BEFORE a month rather than partway through: a month must be all-or-nothing so
                // the watermark keeps meaning "everything from here forward is complete".
                if (rate.Remaining is int remaining && remaining - users.Count < BackfillRateLimitReserve)
                {
                    _logger.LogInformation(
                        "Per-user backfill for '{Slug}' pausing before {Year}-{Month:00}: {Remaining} rate-limit remaining " +
                        "would not cover {Users} users while holding {Reserve} in reserve. Resumes next cycle.",
                        enterprise.Slug, year, month, remaining, users.Count, BackfillRateLimitReserve);
                    paused = true;
                    break;
                }

                foreach (var u in users)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(u.GitHubComLogin)) continue;

                    var usage = await client.GetUsageForUserAsync(enterprise.Slug, u.GitHubComLogin, year, month, ct);
                    if (usage?.UsageItems is null) continue;

                    var (ccId, ccName) = usage.CostCenter is not null
                        ? (usage.CostCenter.Id, usage.CostCenter.Name)
                        : userToCc.GetValueOrDefault(u.GitHubComLogin);

                    foreach (var item in usage.UsageItems)
                    {
                        var existing = await db.UsageSnapshots.FirstOrDefaultAsync(x =>
                            x.EnterpriseId == enterprise.Id &&
                            x.Year == year && x.Month == month && x.Day == 1 &&
                            x.UserLogin == u.GitHubComLogin && x.Model == item.Model && x.Sku == item.Sku, ct);

                        if (existing is null)
                        {
                            db.UsageSnapshots.Add(new UsageSnapshot
                            {
                                EnterpriseId = enterprise.Id,
                                SnapshotUtc = now, Year = year, Month = month, Day = 1,
                                UserLogin = u.GitHubComLogin, UserName = u.GitHubComName,
                                CostCenterId = ccId, CostCenterName = ccName,
                                Product = item.Product, Sku = item.Sku, Model = item.Model, UnitType = item.UnitType,
                                NetQuantity = item.NetQuantity, NetAmount = item.NetAmount, GrossAmount = item.GrossAmount,
                                DiscountQuantity = item.DiscountQuantity, DiscountAmount = item.DiscountAmount,
                                PricePerUnit = item.PricePerUnit, GrossQuantity = item.GrossQuantity
                            });
                        }
                        else
                        {
                            // Re-running a month is safe: the same row is refreshed, never duplicated.
                            existing.SnapshotUtc = now;
                            existing.CostCenterId = ccId; existing.CostCenterName = ccName;
                            existing.NetQuantity = item.NetQuantity; existing.NetAmount = item.NetAmount; existing.GrossAmount = item.GrossAmount;
                            existing.DiscountQuantity = item.DiscountQuantity; existing.DiscountAmount = item.DiscountAmount;
                            existing.PricePerUnit = item.PricePerUnit; existing.GrossQuantity = item.GrossQuantity;
                        }
                        written++;
                    }
                }

                await db.SaveChangesAsync(ct);

                // Only NOW is the month complete, so only now does the watermark move.
                await _registry.SetUserBackfillWatermarkAsync(enterprise.Id, year, month, ct);
                enterprise.UserBackfillOldestYear = year;
                enterprise.UserBackfillOldestMonth = month;
                _logger.LogInformation("Backfilled per-user usage for '{Slug}' {Year}-{Month:00} ({Users} users).",
                    enterprise.Slug, year, month, users.Count);
            }

            // Finished the whole plan, so clear the flag HERE rather than leaving it for the next
            // cycle to discover. Waiting would leave the console showing "backfilling…" for up to a
            // full snapshot interval after the work was actually done — the opposite of what that
            // indicator is for.
            if (!paused)
            {
                await _registry.SetUserBackfillEnabledAsync(enterprise.Id, false, ct);
                enterprise.UserBackfillEnabled = false;
                _logger.LogInformation("Per-user backfill complete for '{Slug}'.", enterprise.Slug);
            }

            return written;
        }

        private static Dictionary<string, (string?, string?)> BuildUserCostCenterMap(IReadOnlyList<Models.CostCenter> costCenters)
        {
            var map = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
            foreach (var cc in costCenters)
                foreach (var r in cc.Resources.Where(r => string.Equals(r.Type, "User", StringComparison.OrdinalIgnoreCase)))
                    if (!string.IsNullOrWhiteSpace(r.Name)) map[r.Name] = (cc.Id, cc.Name);
            return map;
        }
    }
}
