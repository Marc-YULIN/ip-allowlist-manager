using System.Net;

namespace Allowlist.Core;

public sealed record IpDecision(bool Allowed, bool Blocked, Guid? MatchedEntryId, string? MatchedCidr)
{
    public static IpDecision Evaluate(IPAddress ip, IEnumerable<IpEntry> activeEntries)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var match = activeEntries.Where(x => x.State == EntryState.Active)
            .Where(x => IPNetwork.TryParse(x.Cidr, out var n) && n.Contains(ip))
            .OrderByDescending(x => x.Kind == EntryKind.Block)
            .ThenByDescending(x => IPNetwork.Parse(x.Cidr).PrefixLength).ThenBy(x => x.Id).FirstOrDefault();
        return new(match?.Kind == EntryKind.Allow, match?.Kind == EntryKind.Block, match?.Id, match?.Cidr);
    }
}
