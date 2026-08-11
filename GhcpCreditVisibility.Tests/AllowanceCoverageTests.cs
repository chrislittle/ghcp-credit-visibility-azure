using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Tests
{
    /// <summary>
    /// Consumed (gross) vs billable (net).
    ///
    /// Confirmed against the live API: a month where everyone stays inside their included allowance
    /// has netAmount summing to ZERO while real consumption happened. Every display path in this app
    /// sums net, so such a month renders as "$0.00" — indistinguishable from nobody touching Copilot,
    /// and an easy way for someone to conclude a rollout failed when usage is healthy and covered.
    ///
    /// The one distinction these tests exist to protect: "no usage" vs "usage that cost nothing".
    /// </summary>
    public class AllowanceCoverageTests
    {
        [Fact]
        public void Fully_covered_is_not_the_same_as_idle()
        {
            var covered = new UsageQueryService.AllowanceCoverage(Net: 0m, Gross: 250m);
            var idle = new UsageQueryService.AllowanceCoverage(Net: 0m, Gross: 0m);

            // Both total $0 billable. They must never present identically.
            Assert.True(covered.IsFullyCovered);
            Assert.False(covered.IsGenuinelyIdle);

            Assert.True(idle.IsGenuinelyIdle);
            Assert.False(idle.IsFullyCovered);

            Assert.Equal(250m, covered.Covered);
            Assert.Equal(0m, idle.Covered);
        }

        [Fact]
        public void Fully_covered_reports_all_consumption_as_covered()
        {
            var c = new UsageQueryService.AllowanceCoverage(Net: 0m, Gross: 80m);
            Assert.Equal(80m, c.Covered);
            Assert.Equal(100.0, c.CoveredPct);
            Assert.False(c.IsPartiallyCovered);   // nothing billable, so not "partial"
        }

        [Fact]
        public void Partially_covered_reports_the_absorbed_share()
        {
            var c = new UsageQueryService.AllowanceCoverage(Net: 30m, Gross: 100m);
            Assert.True(c.IsPartiallyCovered);
            Assert.False(c.IsFullyCovered);
            Assert.Equal(70m, c.Covered);
            Assert.Equal(70.0, c.CoveredPct);
        }

        [Fact]
        public void No_allowance_applied_is_neither_covered_nor_idle()
        {
            var c = new UsageQueryService.AllowanceCoverage(Net: 100m, Gross: 100m);
            Assert.False(c.IsFullyCovered);
            Assert.False(c.IsPartiallyCovered);
            Assert.False(c.IsGenuinelyIdle);
            Assert.Equal(0m, c.Covered);
            Assert.Equal(0.0, c.CoveredPct);
        }

        /// <summary>
        /// Net above gross would mean a surcharge, not a discount. Rather than surface a negative
        /// "covered" figure, clamp — there is no such billing concept to represent.
        /// </summary>
        [Fact]
        public void Net_exceeding_gross_never_yields_a_negative_covered_amount()
        {
            var c = new UsageQueryService.AllowanceCoverage(Net: 50m, Gross: 20m);
            Assert.Equal(0m, c.Covered);
            Assert.False(c.IsPartiallyCovered);
            Assert.Equal(0.0, c.CoveredPct);
        }

        /// <summary>Zero gross must not divide by zero when computing the share.</summary>
        [Fact]
        public void Zero_gross_does_not_divide_by_zero()
        {
            var c = new UsageQueryService.AllowanceCoverage(Net: 0m, Gross: 0m);
            Assert.Equal(0.0, c.CoveredPct);
        }

        /// <summary>
        /// The real July shape from the live probe: seven usage items, gross populated, net summing
        /// to zero because the allowance absorbed everything.
        /// </summary>
        [Fact]
        public void The_observed_july_shape_is_reported_as_covered_not_idle()
        {
            var july = new UsageQueryService.AllowanceCoverage(Net: 0m, Gross: 12.47m);
            Assert.True(july.IsFullyCovered);
            Assert.False(july.IsGenuinelyIdle);
            Assert.Equal(12.47m, july.Covered);
        }
    }
}
