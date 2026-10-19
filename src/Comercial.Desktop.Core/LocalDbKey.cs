using System.Security.Cryptography;

namespace Comercial.Desktop.Core;

/// <summary>
/// Chave do banco local: 32 bytes aleatórios gerados no primeiro uso, guardados protegidos pelo Windows (DPAPI,
/// usuário atual). Quem copiar só o arquivo local.db para outro computador não consegue ler as vendas.
/// </summary>
public static class LocalDbKey
{
    private static readonly byte[] Entropy = "Comercial.LocalDb.v1"u8.ToArray();

    public static string? GetOrCreate(string keyPath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (File.Exists(keyPath))
        {
            try { return Convert.ToHexString(ProtectedData.Unprotect(File.ReadAllBytes(keyPath), Entropy, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { throw new InvalidOperationException("A chave do banco local não pôde ser aberta (outro usuário do Windows?). Entre com o mesmo usuário ou reinstale o caixa."); }
        }
        var raw = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        File.WriteAllBytes(keyPath, ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser));
        return Convert.ToHexString(raw);
    }
}
