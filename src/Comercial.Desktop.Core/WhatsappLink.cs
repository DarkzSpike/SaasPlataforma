namespace Comercial.Desktop.Core;

/// <summary>Link do WhatsApp do botão de atendimento (número definido no painel admin).</summary>
public static class WhatsappLink
{
    public const string DefaultNumber = "5547992841812";
    public const string DefaultMessage = "Olá! Preciso de ajuda com o sistema.";

    /// <summary>Monta https://wa.me/... com a mensagem e, se logado, a loja e o usuário (o atendente já sabe quem é).</summary>
    public static string? Build(string? number, string? message, string? tenantCode = null, string? user = null)
    {
        var digits = new string((number ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 12) return null;
        var text = string.IsNullOrWhiteSpace(message) ? DefaultMessage : message.Trim();
        if (!string.IsNullOrWhiteSpace(tenantCode)) text += $"\n\nLoja: {tenantCode.Trim()}" + (string.IsNullOrWhiteSpace(user) ? "" : $" • Usuário: {user.Trim()}");
        return $"https://wa.me/{digits}?text={Uri.EscapeDataString(text)}";
    }
}
