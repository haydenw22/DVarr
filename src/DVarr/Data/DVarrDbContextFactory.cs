using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DVarr.Data;

/// <summary>
/// Design-time factory so `dotnet ef migrations add …` can build the context WITHOUT
/// executing Program.cs (whose startup runs MigrateAsync — which would be circular at
/// design time). Only used by the EF tooling.
/// </summary>
public sealed class DVarrDbContextFactory : IDesignTimeDbContextFactory<DVarrDbContext>
{
    public DVarrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DVarrDbContext>()
            .UseSqlite("Data Source=dvarr-design.db")
            .Options;
        return new DVarrDbContext(options);
    }
}
