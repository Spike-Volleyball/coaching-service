using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Coaching.Infrastructure.Data.Context;

// EF tooling (migrations, the CI migration bundle) builds the context here instead of booting the app,
// whose startup refuses to run without JWT settings the build has no reason to hold. The deploy passes
// the real connection string to the bundle.
public class CoachingDbContextFactory : IDesignTimeDbContextFactory<CoachingDbContext>
{
    public CoachingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<CoachingDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=coaching;Username=volleyer_user;Password=volleyer_password_dev;");

        return new CoachingDbContext(optionsBuilder.Options);
    }
}
