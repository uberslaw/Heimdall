# Patch: Heimdall.Api/Data/HeimdallDbContext.cs

## 1. New `DbSet`

Next to `FleetMetricSnapshots` (same file, confirmed exact current text):

```diff
     public DbSet<FleetDashboardMachine> FleetDashboardMachines => Set<FleetDashboardMachine>();
     public DbSet<FleetMetricSnapshot> FleetMetricSnapshots => Set<FleetMetricSnapshot>();
+    public DbSet<TuflowRunRecord> TuflowRunRecords => Set<TuflowRunRecord>();
     public DbSet<CustomTheme> CustomThemes => Set<CustomTheme>();
```

## 2. `OnModelCreating` entry

Next to the `FleetMetricSnapshot` configuration block (same
cascade-delete-on-Machine pattern, same unique/composite index shape as
`UserSession`'s `{ MachineId, StartedAtUtc }` index):

```diff
         modelBuilder.Entity<FleetMetricSnapshot>(e =>
         {
             e.HasIndex(x => new { x.MachineId, x.SampledAtUtc });
             e.HasIndex(x => x.SampledAtUtc);
             e.HasOne(x => x.Machine)
                 .WithMany()
                 .HasForeignKey(x => x.MachineId)
                 .OnDelete(DeleteBehavior.Cascade);
         });
+
+        modelBuilder.Entity<TuflowRunRecord>(e =>
+        {
+            e.HasIndex(x => x.RunId).IsUnique();
+            e.HasIndex(x => new { x.MachineId, x.RequestedUtc });
+            e.HasOne(x => x.Machine)
+                .WithMany()
+                .HasForeignKey(x => x.MachineId)
+                .OnDelete(DeleteBehavior.Cascade);
+        });
```

No `using` changes needed — `TuflowRunRecord` lives in the same
`Heimdall.Api.Data` namespace as `Machine`/`FleetMetricSnapshot`/etc.
(see `Entities.cs.patch.md` section 3).
