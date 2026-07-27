using GhcpCreditVisibility.Data;
using GhcpCreditVisibility.Pages;
using GhcpCreditVisibility.Services;

namespace GhcpCreditVisibility.Tests;

/// <summary>
/// Pins the dashboard's exceptions-first budget behavior: small deployments see every meter
/// (no regression for a single-cost-center manager), large ones see only over/near-limit budgets,
/// worst first, capped — a SeesAll exec across enterprises must never face a wall of green meters
/// that buries the red ones.
/// </summary>
public class BudgetPartitionTests
{
    private static BudgetService.BudgetStatus Budget(decimal amount, decimal actual, string name = "cc", long ent = 1)
        => new(BudgetScopes.CostCenter, name, name, amount, actual, ent, "Ent" + ent);

    [Fact]
    public void Small_scale_shows_every_budget_unchanged()
    {
        var all = Enumerable.Range(1, IndexModel.BudgetInlineLimit)
            .Select(i => Budget(100, 10 * i, $"cc{i}"))
            .ToList();

        var (shown, hidden, exceptionsMode) = IndexModel.PartitionBudgets(all);

        Assert.False(exceptionsMode);
        Assert.Equal(all.Count, shown.Count); // exactly today's view, on-track meters included
        Assert.Equal(0, hidden);
    }

    [Fact]
    public void At_scale_only_over_and_near_limit_get_meters_worst_first()
    {
        var all = new List<BudgetService.BudgetStatus>
        {
            Budget(100, 50,  "ok-1"),
            Budget(100, 80,  "watch-1"),   // warn (75-90%) is deliberately NOT dashboard-worthy
            Budget(100, 95,  "critical-1"),
            Budget(100, 120, "over-1"),
            Budget(100, 105, "over-2"),
            Budget(100, 92,  "critical-2"),
            Budget(100, 10,  "ok-2"),
        };

        var (shown, hidden, exceptionsMode) = IndexModel.PartitionBudgets(all);

        Assert.True(exceptionsMode);
        Assert.Equal(0, hidden);
        // over (by % desc), then critical (by % desc); ok and warn collapse into the summary counts.
        Assert.Equal(new[] { "over-1", "over-2", "critical-1", "critical-2" },
            shown.Select(b => b.CostCenterId).ToArray());
    }

    [Fact]
    public void Attention_list_is_capped_and_reports_the_hidden_count()
    {
        var all = Enumerable.Range(1, IndexModel.BudgetAttentionCap + 3)
            .Select(i => Budget(100, 100 + i, $"over-{i}")) // all over budget
            .Concat(new[] { Budget(100, 10, "ok-1") })
            .ToList();

        var (shown, hidden, exceptionsMode) = IndexModel.PartitionBudgets(all);

        Assert.True(exceptionsMode);
        Assert.Equal(IndexModel.BudgetAttentionCap, shown.Count);
        Assert.Equal(3, hidden); // "...and 3 more need attention"
        Assert.Equal("over-" + (IndexModel.BudgetAttentionCap + 3), shown[0].CostCenterId); // worst first
    }

    [Fact]
    public void Healthy_at_scale_shows_no_meters_at_all()
    {
        var all = Enumerable.Range(1, 20).Select(i => Budget(100, 40, $"ok-{i}")).ToList();

        var (shown, hidden, exceptionsMode) = IndexModel.PartitionBudgets(all);

        Assert.True(exceptionsMode);
        Assert.Empty(shown); // just the summary chips — three lines instead of twenty meters
        Assert.Equal(0, hidden);
    }
}
