using System.Net;
using System.Net.Sockets;

namespace Allowlist.Core;

public static class CidrNormalizer
{
    public static bool TryNormalize(string input, out IPNetwork network, out string? error)
    {
        network = default;
        error = null;
        var parts = (input ?? "").Trim().Split('/');
        if (parts[0].IndexOfAny(['%', '[', ']']) >= 0 || (!parts[0].Contains(':') && (parts[0].Split('.').Length != 4 ||
            parts[0].Split('.').Any(x => x.Length == 0 || x.Any(c => c is < '0' or > '9') || (x.Length > 1 && x[0] == '0')))))
            return Fail("Invalid IP address.", out error);
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip))
            return Fail("Invalid IP address.", out error);
        var bits = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = bits;
        if (parts.Length == 2 && (parts[1].Any(c => c is < '0' or > '9') || !int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > bits))
            return Fail("Invalid prefix length.", out error);
        if (ip.IsIPv4MappedToIPv6)
        {
            if (prefix < 96) return Fail("Mapped IPv6 prefix must be at least 96.", out error);
            ip = ip.MapToIPv4();
            prefix -= 96;
        }
        if (prefix == 0) return Fail("Default routes are not allowed.", out error);
        var bytes = ip.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] &= (byte)~(1 << (7 - bit % 8));
        var baseAddress = new IPAddress(bytes);
        if (!baseAddress.Equals(ip))
            return Fail($"Host bits must be zero; did you mean {baseAddress}/{prefix}.", out error);
        return IPNetwork.TryParse($"{baseAddress}/{prefix}", out network);
    }

    private static bool Fail(string message, out string? error) { error = message; return false; }
}
