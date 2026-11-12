using System.Net;
using Allowlist.Core;

namespace Allowlist.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("192.0.2.1", "192.0.2.1/32")]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("2001:0DB8::1", "2001:db8::1/128")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("::ffff:192.0.2.1", "192.0.2.1/32")]
    [InlineData("::ffff:c000:200/120", "192.0.2.0/24")]
    [InlineData("::192.0.2.1", "::192.0.2.1/128")]
    [InlineData("::ffff:0:192.0.2.1", "::ffff:0:192.0.2.1/128")]
    public void TryNormalize_ValidInput_ReturnsCanonicalNetwork(string input, string expected)
    {
        Assert.True(CidrNormalizer.TryNormalize(input, out var network, out var error), error);
        Assert.Equal(expected, network.ToString());
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("::ffff:0.0.0.0/96")]
    [InlineData("::ffff:192.0.2.1/95")]
    [InlineData("192.0.2.1/24")]
    [InlineData("2001:db8::1/64")]
    [InlineData("10.1")]
    [InlineData("010.0.0.1")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/33")]
    [InlineData("fe80::1%1")]
    [InlineData("[::1]")]
    [InlineData("garbage")]
    public void TryNormalize_InvalidOrUnsafeInput_ReturnsError(string input)
    { Assert.False(CidrNormalizer.TryNormalize(input, out _, out var error)); Assert.NotNull(error); }

    [Fact]
    public void TryNormalize_HostBits_SuggestsNetwork()
    { CidrNormalizer.TryNormalize("10.0.0.5/24", out _, out var error); Assert.Contains("did you mean 10.0.0.0/24", error); }

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/8", "duplicate")]
    [InlineData("10.1.0.0/16", "10.0.0.0/8", "covered_by")]
    [InlineData("10.0.0.0/8", "10.1.0.0/16", null)]
    public void Check_OverlappingNetworks_ReportsDirection(string proposed, string existing, string? error)
    {
        var result = OverlapChecker.Check(IPNetwork.Parse(proposed), [new() { Cidr = existing }]);
        Assert.Equal(error, result.Error);
        if (error is null) Assert.Contains("covers_existing: " + existing, result.Warnings);
    }
    [Fact]
    public void Check_DifferentFamilies_HasNoOverlap()
        => Assert.Null(OverlapChecker.Check(IPNetwork.Parse("2001:db8::/32"), [new() { Cidr = "10.0.0.0/8" }]).Error);

    [Fact]
    public void Evaluate_AllowAndBlockOverlap_BlockTakesPrecedence()
    {
        var block = new IpEntry { Cidr = "192.0.2.0/24", Kind = EntryKind.Block, State = EntryState.Active };
        var result = IpDecision.Evaluate(IPAddress.Parse("192.0.2.1"), [new() { Cidr = "192.0.2.1/32", Kind = EntryKind.Allow, State = EntryState.Active }, block]);
        Assert.False(result.Allowed); Assert.True(result.Blocked); Assert.Equal(block.Id, result.MatchedEntryId);
    }
    [Fact]
    public void Evaluate_MappedIpAndInactiveRules_UsesOnlyActiveRules()
    {
        var entries = new[] { new IpEntry { Cidr = "192.0.2.1/32", Kind = EntryKind.Allow, State = EntryState.Active },
            new IpEntry { Cidr = "192.0.2.0/24", Kind = EntryKind.Block, State = EntryState.PendingAdd } };
        Assert.True(IpDecision.Evaluate(IPAddress.Parse("::ffff:192.0.2.1"), entries).Allowed);
        Assert.False(IpDecision.Evaluate(IPAddress.Parse("198.51.100.1"), entries).Allowed);
    }
    [Fact]
    public void Compute_NormalizedRemoteItems_ReportsMissingAndExtra()
    {
        var drift = DriftCalculator.Compute([new() { Cidr = "192.0.2.1/32" }, new() { Cidr = "2001:db8::1/128" }],
            [new("a", "192.0.2.1"), new("b", "198.51.100.1")], new HashSet<string>());
        Assert.Equal(["2001:db8::1/128"], drift.Missing); Assert.Equal("b", Assert.Single(drift.Extra).Id);
    }
    [Fact]
    public void Compute_InFlightNetworks_ExcludesNormalizedMissingAndExtra()
    {
        var drift = DriftCalculator.Compute([new() { Cidr = "192.0.2.1/32" }, new() { Cidr = "198.51.100.1/32" }],
            [new("pending", "2001:0DB8::1"), new("extra", "203.0.113.1")],
            new HashSet<string> { "192.0.2.1", "2001:db8::1/128" });
        Assert.Equal(["198.51.100.1/32"], drift.Missing);
        Assert.Equal("extra", Assert.Single(drift.Extra).Id);
    }
    [Fact]
    public void Generate_NewApiKeys_ProducesUniqueWellFormedSecrets()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => ApiKeyFormat.Generate()).ToArray();
        Assert.Equal(100, keys.Distinct().Count());
        foreach (var key in keys)
        { Assert.Matches("^ak_[A-Za-z0-9]{8}_[A-Za-z0-9]{32}$", key); Assert.True(ApiKeyFormat.TryParse(key, out var prefix)); Assert.Equal(key.Substring(3, 8), prefix); }
        Assert.Equal(64, ApiKeyFormat.Hash(keys[0]).Length);
        Assert.Equal(ApiKeyFormat.Hash(keys[0]), ApiKeyFormat.Hash(keys[0]));
        Assert.False(ApiKeyFormat.TryParse("ak_abcdefgh_" + new string('!', 32), out _));
    }
    [Fact]
    public void Verify_SaltedPbkdf2Hash_AcceptsOnlyMatchingPassword()
    {
        var hash = PasswordHasher.Hash("Demo-password!");
        Assert.StartsWith("pbkdf2$100000$", hash); Assert.NotEqual(hash, PasswordHasher.Hash("Demo-password!"));
        Assert.True(PasswordHasher.Verify("Demo-password!", hash)); Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.False(PasswordHasher.Verify("Demo-password!", "pbkdf2$1$invalid$hash"));
    }
}
