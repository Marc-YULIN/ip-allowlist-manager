using System.Net;

namespace Allowlist.Core;

public sealed record OverlapResult(string? Error, IReadOnlyList<string> Warnings);
public static class OverlapChecker
{
    public static OverlapResult Check(IPNetwork network, IEnumerable<IpEntry> existing)
    {
        var warnings = new List<string>();
        foreach (var entry in existing)
        {
            if (!IPNetwork.TryParse(entry.Cidr, out var other)) continue;
            if (other == network) return new("duplicate", []);
            if (other.PrefixLength < network.PrefixLength && other.Contains(network.BaseAddress))
                return new("covered_by", [entry.Cidr]);
            if (network.PrefixLength < other.PrefixLength && network.Contains(other.BaseAddress))
                warnings.Add($"covers_existing: {entry.Cidr}");
        }
        return new(null, warnings);
    }
}
