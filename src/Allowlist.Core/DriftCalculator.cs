namespace Allowlist.Core;

public sealed record DriftResult(IReadOnlyList<string> Missing, IReadOnlyList<RemoteListItem> Extra);
public static class DriftCalculator
{
    public static DriftResult Compute(IEnumerable<IpEntry> localActive, IEnumerable<RemoteListItem> remoteItems, IReadOnlySet<string> inFlightCidrs)
    {
        static string Normalize(string cidr) => CidrNormalizer.TryNormalize(cidr, out var n, out _) ? n.ToString() : cidr;
        var local = localActive.Select(x => Normalize(x.Cidr)).ToHashSet(StringComparer.Ordinal);
        var inFlight = inFlightCidrs.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var remote = remoteItems.ToArray();
        var remoteCidrs = remote.Select(x => Normalize(x.Cidr)).ToHashSet(StringComparer.Ordinal);
        return new(local.Except(remoteCidrs).Except(inFlight).Order(StringComparer.Ordinal).ToArray(),
            remote.Where(x => !local.Contains(Normalize(x.Cidr)) && !inFlight.Contains(Normalize(x.Cidr))).ToArray());
    }
}
