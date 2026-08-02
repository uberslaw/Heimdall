using Microsoft.EntityFrameworkCore;

namespace Heimdall.Api.Data;

public class HeimdallDbContext(DbContextOptions<HeimdallDbContext> options) : DbContext(options)
{
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<ProcessRun> ProcessRuns => Set<ProcessRun>();
    public DbSet<TrackingConfig> TrackingConfigs => Set<TrackingConfig>();
    public DbSet<ProcessPause> ProcessPauses => Set<ProcessPause>();
    public DbSet<KnownApp> KnownApps => Set<KnownApp>();
    public DbSet<SoeApp> SoeApps => Set<SoeApp>();
    public DbSet<ProcessGroupAssignment> ProcessGroupAssignments => Set<ProcessGroupAssignment>();
    public DbSet<MetricPolicy> MetricPolicies => Set<MetricPolicy>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<PersonTeam> PersonTeams => Set<PersonTeam>();
    public DbSet<UtilizationCriteria> UtilizationCriteria => Set<UtilizationCriteria>();
    public DbSet<AppLicenseCost> AppLicenseCosts => Set<AppLicenseCost>();
    public DbSet<AppList> AppLists => Set<AppList>();
    public DbSet<AppListEntry> AppListEntries => Set<AppListEntry>();
    public DbSet<AppListAssignment> AppListAssignments => Set<AppListAssignment>();
    public DbSet<AppListAuditLog> AppListAuditLogs => Set<AppListAuditLog>();
    public DbSet<MachineIdentityEvent> MachineIdentityEvents => Set<MachineIdentityEvent>();
    public DbSet<RemoteAccessGroup> RemoteAccessGroups => Set<RemoteAccessGroup>();
    public DbSet<RemoteAccessGroupStaff> RemoteAccessGroupStaff => Set<RemoteAccessGroupStaff>();
    public DbSet<RemoteAccessGroupMachine> RemoteAccessGroupMachines => Set<RemoteAccessGroupMachine>();
    public DbSet<RemoteAccessFavoriteProcess> RemoteAccessFavoriteProcesses => Set<RemoteAccessFavoriteProcess>();
    public DbSet<RemoteAccessViewer> RemoteAccessViewers => Set<RemoteAccessViewer>();
    public DbSet<SessionDrilldownViewer> SessionDrilldownViewers => Set<SessionDrilldownViewer>();
    public DbSet<MachineResourceMetric> MachineResourceMetrics => Set<MachineResourceMetric>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Machine>(e =>
        {
            e.HasIndex(x => x.Hostname).IsUnique();
            e.HasIndex(x => new { x.Region, x.Office });
            e.HasIndex(x => x.Country);
            e.HasIndex(x => x.MachineGuid);
            e.HasIndex(x => x.SmbiosUuid);
        });

        modelBuilder.Entity<MachineIdentityEvent>(e =>
        {
            e.HasIndex(x => new { x.MachineId, x.ObservedAtUtc });
            e.HasOne(x => x.Machine)
                .WithMany(x => x.IdentityEvents)
                .HasForeignKey(x => x.MachineId)
                .OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<SoeApp>(e =>
        {
            e.HasIndex(x => x.ProcessName).IsUnique();
            e.HasIndex(x => x.Category);
        });

        modelBuilder.Entity<ProcessGroupAssignment>(e =>
        {
            e.HasIndex(x => x.ProcessName).IsUnique();
            e.HasIndex(x => x.Group);
        });

        modelBuilder.Entity<ProcessPause>(e =>
        {
            e.HasIndex(x => new { x.TrackingConfigId, x.ProcessName, x.ListKind });
            e.HasOne(x => x.TrackingConfig)
                .WithMany(x => x.ProcessPauses)
                .HasForeignKey(x => x.TrackingConfigId)
                .OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<UtilizationCriteria>(e =>
        {
            e.HasIndex(x => new { x.Scope, x.ScopeValue });
        });

        modelBuilder.Entity<AppLicenseCost>(e =>
        {
            e.HasIndex(x => x.ProcessName).IsUnique();
        });

        modelBuilder.Entity<AppList>(e =>
        {
            e.HasIndex(x => x.Name);
            e.HasOne(x => x.Team)
                .WithMany()
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AppListEntry>(e =>
        {
            e.HasIndex(x => new { x.AppListId, x.ProcessName }).IsUnique();
            e.HasOne(x => x.AppList)
                .WithMany(x => x.Entries)
                .HasForeignKey(x => x.AppListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppListAssignment>(e =>
        {
            e.HasIndex(x => new { x.AppListId, x.Scope, x.ScopeValue });
            e.HasOne(x => x.AppList)
                .WithMany(x => x.Assignments)
                .HasForeignKey(x => x.AppListId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppListAuditLog>(e =>
        {
            e.HasIndex(x => x.Utc);
            e.HasIndex(x => x.MachineHostname);
        });

        modelBuilder.Entity<RemoteAccessGroup>(e =>
        {
            e.HasIndex(x => x.Name);
        });

        modelBuilder.Entity<RemoteAccessGroupStaff>(e =>
        {
            e.HasIndex(x => new { x.GroupId, x.Email }).IsUnique();
            e.HasIndex(x => x.Email);
            e.HasOne(x => x.Group)
                .WithMany(x => x.Staff)
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RemoteAccessGroupMachine>(e =>
        {
            e.HasIndex(x => new { x.GroupId, x.Hostname }).IsUnique();
            e.HasIndex(x => x.Hostname);
            e.HasOne(x => x.Group)
                .WithMany(x => x.Machines)
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RemoteAccessFavoriteProcess>(e =>
        {
            e.HasIndex(x => new { x.GroupId, x.ProcessName }).IsUnique();
            e.HasOne(x => x.Group)
                .WithMany(x => x.FavoriteProcesses)
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RemoteAccessViewer>(e =>
        {
            e.HasIndex(x => new { x.GroupId, x.ViewerId }).IsUnique();
            e.HasIndex(x => x.LastHeartbeatUtc);
        });

        modelBuilder.Entity<SessionDrilldownViewer>(e =>
        {
            e.HasIndex(x => new { x.Hostname, x.ViewerId }).IsUnique();
            e.HasIndex(x => x.LastHeartbeatUtc);
        });

        modelBuilder.Entity<MachineResourceMetric>(e =>
        {
            e.HasIndex(x => x.MachineId).IsUnique();
            e.HasOne(x => x.Machine)
                .WithMany()
                .HasForeignKey(x => x.MachineId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
