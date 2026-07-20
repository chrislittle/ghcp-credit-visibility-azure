using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GhcpCreditVisibility.Data
{
    /// <summary>
    /// Design-time factory so `dotnet ef migrations add` can construct the context without the web host.
    /// The connection string here is a placeholder — migration <em>generation</em> never connects to a
    /// database; at runtime the app builds the context via AddDbContextFactory with the real (managed
    /// identity) connection string and applies migrations with Database.Migrate().
    /// </summary>
    public sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
    {
        public BillingDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<BillingDbContext>()
                .UseSqlServer("Server=(localdb)\\design;Database=GhcpCreditVisibilityDesign;Trusted_Connection=True;")
                .Options;
            return new BillingDbContext(options);
        }
    }
}
