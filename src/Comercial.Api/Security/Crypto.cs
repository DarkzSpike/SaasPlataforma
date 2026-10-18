using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Comercial.Api.Security;

/// <summary>Hash de senha Argon2id. Formato: argon2id$v=19$m=..,t=..,p=..$salt$hash</summary>
public sealed class PasswordHasher(IConfiguration config)
{
    private readonly int _memoryKb = config.GetValue("Security:Argon2MemoryKb", 65536);
    private readonly int _iterations = config.GetValue("Security:Argon2Iterations", 3);
    private const int Parallelism = 2;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(password, salt, _memoryKb, _iterations, Parallelism);
        return $"argon2id$v=19$m={_memoryKb},t={_iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 5 || parts[0] != "argon2id") return false;
        var prm = parts[2].Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0], p => int.Parse(p[1]));
        var salt = Convert.FromBase64String(parts[3]);
        var expected = Convert.FromBase64String(parts[4]);
        var actual = Compute(password, salt, prm["m"], prm["t"], prm["p"]);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, int m, int t, int p)
    {
        using var a = new Argon2id(Encoding.UTF8.GetBytes(password)) { Salt = salt, MemorySize = m, Iterations = t, DegreeOfParallelism = p };
        return a.GetBytes(32);
    }

    public static string? ValidatePolicy(string password) =>
        password.Length < 10 ? "A senha deve ter pelo menos 10 caracteres." :
        password.Length > 128 ? "Senha muito longa." :
        !password.Any(char.IsLetter) || !password.Any(char.IsDigit) ? "A senha deve conter letras e números." : null;
}

/// <summary>TOTP (RFC 6238) — SHA1, 6 dígitos, passo de 30s.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => Base32(RandomNumberGenerator.GetBytes(20));

    public static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / 30;

    public static string Code(string base32Secret, long step)
    {
        var key = FromBase32(base32Secret);
        var msg = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var h = HMACSHA1.HashData(key, msg);
        var o = h[^1] & 0x0F;
        var bin = ((h[o] & 0x7F) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    /// <summary>Retorna o passo validado (tolerância ±1) ou null.</summary>
    public static long? Verify(string base32Secret, string? code, DateTimeOffset now)
    {
        if (code is null || code.Length != 6 || !code.All(char.IsDigit)) return null;
        var step = CurrentStep(now);
        for (var d = -1; d <= 1; d++)
        {
            var c = Code(base32Secret, step + d);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c), Encoding.ASCII.GetBytes(code))) return step + d;
        }
        return null;
    }

    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] FromBase32(string s)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in s.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(ch); bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }
}

public static class Hashing
{
    public static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    public static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    public static string NewOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
