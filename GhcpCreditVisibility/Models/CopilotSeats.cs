using System.Text.Json.Serialization;

namespace GhcpCreditVisibility.Models
{
    /// <summary>
    /// Response for "List Copilot seats for an enterprise"
    /// (GET /enterprises/{enterprise}/copilot/billing/seats).
    ///
    /// This is the ONLY source of a true Copilot seat count. The obvious-looking alternatives are
    /// all wrong, each confirmed against the live API on 2026-08-13:
    ///   * <c>consumed-licenses</c> counts GHEC LICENCES — a different population. A demo enterprise
    ///     returned 8 licences against 3 Copilot seats.
    ///   * <c>consumed-licenses.license_type</c> is the GHEC seat type ("Enterprise"), not the
    ///     Copilot plan — it read "Enterprise" while the Copilot SKU said "Copilot Business".
    ///   * The Copilot seat line on <c>/settings/billing/usage</c> is PRORATED billing in UserMonths
    ///     (0.48387 = 15/31 of one seat), which is a cost, not a count.
    ///   * <c>/enterprises/{ent}/copilot/billing</c> (without /seats) is 404 at enterprise level.
    /// </summary>
    public class CopilotSeatsResponse
    {
        /// <summary>Total seats across all pages, as reported by GitHub.</summary>
        [JsonPropertyName("total_seats")]
        public int TotalSeats { get; set; }

        [JsonPropertyName("seats")]
        public List<CopilotSeat> Seats { get; set; } = new();
    }

    /// <summary>
    /// One assigned Copilot seat.
    ///
    /// DELIBERATELY MAPS ONE FIELD. The live response also carries <c>assignee</c>,
    /// <c>last_activity_at</c>, <c>last_activity_editor</c>, <c>last_authenticated_at</c>,
    /// <c>created_at</c>, <c>updated_at</c> and <c>pending_cancellation_date</c> — per-person activity
    /// data this app has no use for. Not mapping it is the cheapest way to guarantee it never reaches
    /// the database or a log: an unmapped field cannot leak.
    /// </summary>
    public class CopilotSeat
    {
        /// <summary>
        /// The Copilot plan this seat is on, verbatim from GitHub. Observed lowercase ("business");
        /// "enterprise" is the other documented value. Kept as GitHub's own string rather than mapped
        /// to an enum so a plan this app has never seen is RECORDED rather than guessed at — the same
        /// reasoning as <c>BudgetScopes.Unknown</c>.
        ///
        /// This is what makes a mixed-plan enterprise computable: Business seats include 1,900
        /// credits and Enterprise seats 3,900, so capacity is a sum over plans, not one multiplication.
        /// </summary>
        [JsonPropertyName("plan_type")]
        public string? PlanType { get; set; }
    }
}
