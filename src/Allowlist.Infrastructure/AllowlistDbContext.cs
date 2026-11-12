using Allowlist.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Allowlist.Infrastructure;

public sealed class AllowlistDbContext(DbContextOptions<AllowlistDbContext> options) : DbContext(options)
{
    public DbSet<ManagedEnvironment> Environments => Set<ManagedEnvironment>();
    public DbSet<IpEntry> Entries => Set<IpEntry>();
    public DbSet<RemoteOperation> Operations => Set<RemoteOperation>();
    public DbSet<RemoteDispatchLog> DispatchLogs => Set<RemoteDispatchLog>();
    public DbSet<DriftSnapshot> Snapshots => Set<DriftSnapshot>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<IntegrationClient> IntegrationClients => Set<IntegrationClient>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var b = modelBuilder;
        b.Entity<ManagedEnvironment>().HasIndex(x => x.Code).IsUnique();
        b.Entity<AdminUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<IntegrationClient>().HasIndex(x => x.Name).IsUnique();
        b.Entity<IntegrationClient>().HasIndex(x => x.KeyPrefix).IsUnique();
        b.Entity<IpEntry>().HasIndex(x => new { x.EnvironmentId, x.Cidr, x.Kind });
        b.Entity<IpEntry>().Property<string>("LiveKey");
        b.Entity<IpEntry>().HasIndex("LiveKey").IsUnique();
        b.Entity<IpEntry>().HasOne<ManagedEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId);
        b.Entity<RemoteOperation>().HasOne<IpEntry>().WithMany().HasForeignKey(x => x.EntryId);
        b.Entity<RemoteOperation>().HasOne<ManagedEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId);
        b.Entity<RemoteOperation>().HasIndex(x => new { x.State, x.NextAttemptAt });
        b.Entity<RemoteDispatchLog>().HasOne<RemoteOperation>().WithMany().HasForeignKey(x => x.OperationId);
        b.Entity<RemoteDispatchLog>().HasIndex(x => new { x.OperationId, x.At });
        b.Entity<DriftSnapshot>().HasOne<ManagedEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId);
        b.Entity<AuditLog>().HasIndex(x => x.At);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<IpEntry>().Where(x => x.State is EntityState.Added or EntityState.Modified))
            entry.Property("LiveKey").CurrentValue = entry.Entity.State == EntryState.Removed ? null
                : $"{entry.Entity.EnvironmentId:N}:{entry.Entity.Cidr}:{entry.Entity.Kind}";
        return base.SaveChangesAsync(cancellationToken);
    }
}

public sealed class UtcTicksConverter() : ValueConverter<DateTime, long>(
    x => x.ToUniversalTime().Ticks, x => new DateTime(x, DateTimeKind.Utc));
