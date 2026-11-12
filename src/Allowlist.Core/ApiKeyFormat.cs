using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Allowlist.Core;

public static class ApiKeyFormat
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly SearchValues<char> Characters = SearchValues.Create(Alphabet);
    public static string Generate() => $"ak_{RandomPart(8)}_{RandomPart(32)}";
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    public static bool TryParse(string key, out string prefix)
    {
        prefix = "";
        if (key.Length != 44 || !key.StartsWith("ak_", StringComparison.Ordinal) || key[11] != '_') return false;
        if (!key.AsSpan(3, 8).ContainsAnyExcept(Characters) && !key.AsSpan(12).ContainsAnyExcept(Characters))
        { prefix = key.Substring(3, 8); return true; }
        return false;
    }
    private static string RandomPart(int length) => new(Enumerable.Range(0, length)
        .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
}

public static class PasswordHasher
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2" || parts[1] != "100000") return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            return salt.Length == 16 && expected.Length == 32 && CryptographicOperations.FixedTimeEquals(expected,
                Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32));
        }
        catch (FormatException) { return false; }
    }
}
