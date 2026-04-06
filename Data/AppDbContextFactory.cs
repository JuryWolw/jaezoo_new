using JaeZoo.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JaeZoo.Server.Data;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
                   ?? "Host=localhost;Port=5432;Database=jaezoo_local;Username=postgres;Password=postgres";
        var b = new DbContextOptionsBuilder<AppDbContext>();
        b.UseNpgsql(conn);
        return new AppDbContext(b.Options);
    }
}
