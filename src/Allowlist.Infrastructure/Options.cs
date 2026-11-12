using System.ComponentModel.DataAnnotations;

namespace Allowlist.Infrastructure;

public sealed class DatabaseOptions
{
    [RegularExpression("Sqlite|Postgres")] public string Provider { get; set; } = "Sqlite";
}
public sealed class CloudflareOptions
{
    [RegularExpression("Fake|Live")] public string Mode { get; set; } = "Fake";
    [Url] public string ApiBase { get; set; } = "https://api.cloudflare.com/client/v4";
    public string AccountId { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public bool AllowRemoveUnknown { get; set; }
    [Range(1, 20)] public int MaxAttempts { get; set; } = 6;
    public FakeOptions Fake { get; set; } = new();
}
public sealed class FakeOptions
{
    [Range(0, 1)] public double FailureRate { get; set; }
    [Range(0, 1)] public double TimeoutRate { get; set; }
}
public sealed class SeedOptions
{
    [Required] public string AdminUsername { get; set; } = "admin";
    public string? AdminPassword { get; set; }
    public string? ViewerPassword { get; set; }
    public string DevAllowListId { get; set; } = "demo-dev-allow";
    public string DevBlockListId { get; set; } = "demo-dev-block";
    public string StagingAllowListId { get; set; } = "demo-staging-allow";
    public string StagingBlockListId { get; set; } = "demo-staging-block";
    public string ProdAllowListId { get; set; } = "demo-prod-allow";
    public string ProdBlockListId { get; set; } = "demo-prod-block";
}
