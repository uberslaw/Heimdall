using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Data;

public class HeimdallDbContext(DbContextOptions<HeimdallDbContext> options) : DbContext(options)
{
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<ProcessRun> ProcessRuns => Set<ProcessRun>();
    public DbSet<TrackingConfig> TrackingConfigs => Set<TrackingConfig>();
    public DbSet<KnownApp> KnownApps => Set<KnownApp>();
    public DbSet<MetricPolicy> MetricPolicies => Set<MetricPolicy>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<PersonTeam> PersonTeams => Set<PersonTeam>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Machine>(e =>
        {
            e.HasIndex(x => x.Hostname).IsUnique();
            e.HasIndex(x => new { x.Region, x.Office });
            e.HasIndex(x => x.Country);
        });

        modelBuilder.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.ExternalEventId).IsUnique();
            e.HasIndex(x => new { x.MachineId, x.StartedAtUtc });
        });

        modelBuilder.Entity<ProcessRun>(e =>
        {
            e.HasIndex(x => x.ExternalRunId).IsUnique();
            e.HasIndex(x => new { x.MachineId, x.ProcessName, x.StartedAtUtc });
        });

        modelBuilder.Entity<KnownApp>(e =>
        {
            e.HasIndex(x => x.ProcessName).IsUnique();
        });

        modelBuilder.Entity<MetricPolicy>(e =>
        {
            e.HasIndex(x => new { x.MetricType, x.Scope, x.ScopeValue });
        });

        modelBuilder.Entity<Team>(e =>
        {
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.Code);
            e.HasOne(x => x.ParentTeam)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentTeamId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PersonTeam>(e =>
        {
            e.HasIndex(x => new { x.Username, x.Domain });
            e.HasIndex(x => x.TeamId);
            e.HasOne(x => x.Team)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
