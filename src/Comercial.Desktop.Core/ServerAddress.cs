namespace Comercial.Desktop.Core;

/// <summary>
/// Endereço do servidor digitado no caixa (mesma regra do app do celular): HTTPS em qualquer endereço;
/// HTTP só na rede local (localhost, 10.x, 172.16–31.x, 192.168.x), porque senha e vendas não podem trafegar abertas na internet.
/// </summary>
public static class ServerAddress
{
    /// <summary>"192.168.0.10:5080" vira "http://192.168.0.10:5080"; "abc.trycloudflare.com" vira "https://abc.trycloudflare.com".</summary>
    public static bool TryNormalize(string? input, out string url, out string? error)
    {
        url = ""; error = null;
        var s = (input ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) { error = "Informe o endereço do servidor."; return false; }
        if (!s.Contains("://"))
        {
            var host = s.Split(':', '/')[0];
            s = (IsLocal(host) ? "http://" : "https://") + s;
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || string.IsNullOrEmpty(u.Host))
        { error = "Endereço inválido. Exemplo: https://servidor.com.br ou 192.168.0.10:5080"; return false; }
        if (u.Scheme == "http" && !IsLocal(u.Host))
        { error = "Pela internet use https://. Endereço http:// só é aceito na rede local."; return false; }
        if (!string.IsNullOrEmpty(u.Query) || u.AbsolutePath != "/") { error = "Use só o endereço, sem caminho (ex.: https://servidor.com.br)."; return false; }
        url = u.GetLeftPart(UriPartial.Authority);
        return true;
    }

    public static bool IsLocal(string host)
    {
        if (host is "localhost" or "127.0.0.1" or "::1" or "[::1]") return true;
        var p = host.Split('.');
        if (p.Length != 4 || !p.All(x => byte.TryParse(x, out _))) return false;
        int a = byte.Parse(p[0]), b = byte.Parse(p[1]);
        return a == 10 || a == 192 && b == 168 || a == 172 && b is >= 16 and <= 31;
    }
}
