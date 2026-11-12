using Allowlist.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Allowlist.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAllowlistInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<DatabaseOptions>().BindConfiguration("Database").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<CloudflareOptions>().BindConfiguration("Cloudflare").ValidateDataAnnotations()
            .Validate(x => x.Fake.FailureRate is >= 0 and <= 1 && x.Fake.TimeoutRate is >= 0 and <= 1, "Fake probabilities must be between zero and one.")
            .Validate(x => x.Mode != "Live" || (!string.IsNullOrWhiteSpace(x.AccountId) && !string.IsNullOrWhiteSpace(x.ApiToken)), "Live mode requires account and token.").ValidateOnStart();
        services.AddOptions<SeedOptions>().BindConfiguration("Seed").ValidateDataAnnotations().ValidateOnStart();
        services.AddDbContext<AllowlistDbContext>((sp, b) =>
        {
            if (sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Provider == "Postgres")
                b.UseNpgsql(configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Postgres connection string is required."));
            else b.UseSqlite(configuration.GetConnectionString("Sqlite") ?? "Data Source=appdata/allowlist.db");
        });
        services.AddHttpClient<CloudflareListClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<FakeCloudflareListClient>();
        services.AddScoped<ICloudflareListClient>(sp => sp.GetRequiredService<IOptions<CloudflareOptions>>().Value.Mode == "Live"
            ? sp.GetRequiredService<CloudflareListClient>() : sp.GetRequiredService<FakeCloudflareListClient>());
        services.AddScoped<EntryService>();
        services.AddScoped<DriftReconciler>();
        services.AddScoped<IntegrationClientService>();
        services.AddSingleton<IApiKeyGenerator, ApiKeyGenerator>();
        services.AddScoped<AdminSeeder>();
        services.AddHostedService<RemoteOperationDispatcher>();
        services.AddHostedService<MaintenanceWorker>();
        return services;
    }
}

public sealed class MaintenanceWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogCycleFailure = LoggerMessage.Define(LogLevel.Error, new EventId(2, "MaintenanceCycleFailure"),
        "Maintenance cycle failed; the next cycle will retry.");
    private static readonly Action<ILogger, Guid, Exception?> LogEnvironmentFailure = LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(4, "EnvironmentReconcileFailure"),
        "Reconciliation failed for environment {EnvironmentId}; other environments will continue.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextReconcile = clock.GetUtcNow().AddMinutes(10);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EntryService>().ExpireAsync(stoppingToken);
                var db = scope.ServiceProvider.GetRequiredService<AllowlistDbContext>();
                var reconciler = scope.ServiceProvider.GetRequiredService<DriftReconciler>();
                foreach (var id in await db.Environments.Select(x => x.Id).ToListAsync(stoppingToken))
                {
                    try
                    {
                        if (clock.GetUtcNow() >= nextReconcile)
                            await reconciler.ReconcileAsync(id, false, new("scheduled", ActorType.System, Guid.NewGuid().ToString()), stoppingToken);
                        else await reconciler.ResolveUncertainAsync(id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception) { LogEnvironmentFailure(logger, id, null); db.ChangeTracker.Clear(); }
                }
                if (clock.GetUtcNow() >= nextReconcile) nextReconcile = clock.GetUtcNow().AddMinutes(10);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { LogCycleFailure(logger, null); }
            await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
        }
    }
}
