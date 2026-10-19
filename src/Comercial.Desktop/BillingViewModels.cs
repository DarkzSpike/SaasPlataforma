using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

// ============================== Faturas (mensalidade do sistema) ==============================

public sealed class InvoiceRow : ObservableObject
{
    public Guid Id { get; init; }
    public string Period { get; init; } = "";
    public string Due { get; init; } = "";
    public string Amount { get; init; } = "";
    public string Status { get; init; } = "";
    public string Tone { get; init; } = "neutral";
    public bool IsOpen { get; init; }
    public string? PaidInfo { get; init; }
    public string? PixCode { get; init; }
    public BitmapSource? PixImage { get; init; }
    public string? PixExpires { get; init; }
    public bool HasPix => PixCode is not null;
}

/// <summary>
/// Faturas do sistema: PIX (QR na tela), cartão ou boleto (página segura do Mercado Pago no navegador).
/// O pagamento só é considerado confirmado quando o servidor recebe/consulta o Mercado Pago — nunca pela tela.
/// </summary>
public sealed class InvoicesViewModel(ShellViewModel shell) : OwnerPage(shell, "Faturas", "Mensalidade do sistema: pague por PIX, boleto ou cartão (Mercado Pago)")
{
    public ObservableCollection<InvoiceRow> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    private string _banner = "", _info = "";
    public string Banner { get => _banner; set { if (Set(ref _banner, value)) Raise(nameof(HasBanner)); } }
    public bool HasBanner => Banner.Length > 0;
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;

    public RelayCommand PixCommand => new(async p => { if (p is InvoiceRow r) await RunAsync(() => S.Api.PostAsync($"/api/v1/billing/invoices/{r.Id}/pix", new { }), "PIX gerado: pague pelo app do seu banco e depois clique em \"Já paguei\"."); });
    public RelayCommand CheckoutCommand => new(async p => { if (p is InvoiceRow r) await CheckoutAsync(r); });
    public RelayCommand RefreshPaymentCommand => new(async p => { if (p is InvoiceRow r) await RefreshPaymentAsync(r); });
    public RelayCommand CopyPixCommand => RelayCommand.Sync(p =>
    {
        if (p is InvoiceRow { PixCode: { } code }) { try { Clipboard.SetText(code); Info = "Código PIX copiado. Cole no app do seu banco (PIX copia e cola)."; } catch { Error = "Não foi possível copiar."; } }
    });

    private async Task RunAsync(Func<Task<JsonElement>> call, string ok)
    {
        Error = ""; Info = "";
        try { await call(); await LoadAsync(); Info = ok; }
        catch (ApiException ex) { Error = Message(ex); }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }

    private static string Message(ApiException ex) => ex.Code switch
    {
        "payments_not_configured" => "O pagamento online ainda não foi configurado pelo fornecedor do sistema. Fale com o suporte.",
        "payment_provider_error" => "O Mercado Pago não respondeu. Tente novamente em instantes.",
        "invoice_not_open" => "Esta fatura já foi paga ou cancelada.",
        _ => "Não foi possível concluir: " + ex.Code,
    };

    private async Task CheckoutAsync(InvoiceRow r)
    {
        Error = ""; Info = "";
        try
        {
            var v = await S.Api.PostAsync($"/api/v1/billing/invoices/{r.Id}/checkout", new { });
            var url = v.GetProperty("checkoutUrl").GetString();
            // Só abre páginas do próprio Mercado Pago (o servidor também confere).
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https"
                || !(u.Host.EndsWith("mercadopago.com.br", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("mercadopago.com", StringComparison.OrdinalIgnoreCase)))
            { Error = "Link de pagamento inválido."; return; }
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            Info = "Abrimos a página segura do Mercado Pago no navegador. Depois de pagar, clique em \"Já paguei\".";
        }
        catch (ApiException ex) { Error = Message(ex); }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }

    private async Task RefreshPaymentAsync(InvoiceRow r)
    {
        Error = ""; Info = "";
        try
        {
            var v = await S.Api.PostAsync($"/api/v1/billing/invoices/{r.Id}/refresh", new { });
            await LoadAsync();
            Info = v.GetProperty("status").GetString() == "paga"
                ? "Pagamento confirmado. Obrigado!"
                : "Ainda não identificamos o pagamento. PIX costuma confirmar em segundos; boleto pode levar até 3 dias úteis.";
        }
        catch (ApiException ex) { Error = Message(ex); }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }

    private static BitmapSource? Qr(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(Convert.FromBase64String(base64));
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException) { return null; }
    }

    public static InvoiceRow Row(JsonElement i)
    {
        string? Str(string n) => i.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var status = Str("status") ?? "";
        var overdue = i.GetProperty("overdue").GetBoolean();
        var days = i.GetProperty("daysToDue").GetInt32();
        var open = status == "aberta";
        return new InvoiceRow
        {
            Id = i.GetProperty("id").GetGuid(),
            Period = $"{i.GetProperty("periodStart").GetDateTimeOffset().LocalDateTime:dd/MM/yyyy} a {i.GetProperty("periodEnd").GetDateTimeOffset().LocalDateTime:dd/MM/yyyy}",
            Due = i.GetProperty("dueAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM/yyyy"),
            Amount = AppServices.Money(i.GetProperty("amountCents").GetInt64()),
            Status = !open ? (status == "paga" ? "Paga" : "Cancelada") : overdue ? "Vencida" : days <= 3 ? (days <= 0 ? "Vence hoje" : $"Vence em {days} dia(s)") : "Em aberto",
            Tone = !open ? (status == "paga" ? "primary" : "neutral") : overdue ? "danger" : days <= 3 ? "warn" : "accent",
            IsOpen = open,
            PaidInfo = status == "paga" && i.TryGetProperty("paidAt", out var pa) && pa.ValueKind == JsonValueKind.String
                ? $"Paga em {pa.GetDateTimeOffset().LocalDateTime:dd/MM/yyyy HH:mm} • {Str("paidMethod")}" : null,
            PixCode = Str("pixCopyPaste"),
            PixImage = Qr(Str("pixQrBase64")),
            PixExpires = i.TryGetProperty("pixExpiresAt", out var pe) && pe.ValueKind == JsonValueKind.String ? $"Válido até {pe.GetDateTimeOffset().LocalDateTime:dd/MM HH:mm}" : null,
        };
    }

    protected override async Task LoadCoreAsync()
    {
        var list = (await S.Api.GetAsync("/api/v1/billing/invoices")).EnumerateArray().ToList();
        Items.Clear();
        foreach (var i in list) Items.Add(Row(i));
        var next = list.Where(i => i.GetProperty("status").GetString() == "aberta").OrderBy(i => i.GetProperty("dueAt").GetDateTimeOffset()).FirstOrDefault();
        Banner = next.ValueKind != JsonValueKind.Object ? ""
            : next.GetProperty("overdue").GetBoolean() ? $"Você tem uma fatura vencida de {AppServices.Money(next.GetProperty("amountCents").GetInt64())}. Pague para evitar o bloqueio do sistema."
            : next.GetProperty("daysToDue").GetInt32() <= 3 ? $"Sua fatura de {AppServices.Money(next.GetProperty("amountCents").GetInt64())} vence em {Math.Max(0, next.GetProperty("daysToDue").GetInt32())} dia(s)." : "";
        Raise(nameof(IsEmpty));
    }
}

// ============================== Suporte técnico (transparência e consentimento) ==============================

public sealed class SupportOption : ObservableObject
{
    private bool _checked;
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Checked { get => _checked; set => Set(ref _checked, value); }
}

public sealed record SupportRequestRow(Guid Id, string Text, string When);
public sealed record SupportSessionRow(Guid Id, string Text, string Detail, bool Active);

public sealed class SupportViewModel(ShellViewModel shell) : OwnerPage(shell, "Suporte técnico", "Quem acessou sua loja, pedidos de permissão e o que o suporte pode fazer sem pedir")
{
    private static readonly Dictionary<string, string> OpNames = new()
    {
        ["products.write"] = "cadastro de produtos", ["stock.adjust"] = "ajuste de estoque", ["users.manage"] = "usuários",
        ["devices.manage"] = "terminais", ["sales.read"] = "consulta de vendas",
    };
    public static string Ops(string? list) => string.Join(", ", (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(o => OpNames.GetValueOrDefault(o, o)));

    public ObservableCollection<SupportOption> Options { get; } = [];
    public ObservableCollection<SupportRequestRow> Requests { get; } = [];
    public ObservableCollection<SupportSessionRow> Sessions { get; } = [];
    private string _info = "";
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;
    public bool NoRequests => Requests.Count == 0;

    public RelayCommand SaveCommand => new(async _ => await Do(async () =>
    {
        await S.Api.PutAsync("/api/v1/support/preauthorization", new { operations = Options.Where(o => o.Checked).Select(o => o.Key).ToList() });
        Info = Options.Any(o => o.Checked) ? "Autorização salva: o suporte poderá fazer essas alterações sem pedir a cada vez (tudo fica registrado)." : "Autorização removida: toda alteração do suporte vai precisar da sua aprovação.";
    }));
    public RelayCommand ApproveCommand => new(async p => { if (p is SupportRequestRow r) await Do(async () => { await S.Api.PostAsync($"/api/v1/support/elevation-requests/{r.Id}/approve", new { }); Info = "Permissão concedida por tempo limitado."; }); });
    public RelayCommand DenyCommand => new(async p => { if (p is SupportRequestRow r) await Do(async () => { await S.Api.PostAsync($"/api/v1/support/elevation-requests/{r.Id}/deny", new { }); Info = "Pedido recusado."; }); });
    public RelayCommand RevokeCommand => new(async p => { if (p is SupportSessionRow r) await Do(async () => { await S.Api.PostAsync($"/api/v1/support/sessions/{r.Id}/revoke", new { }); Info = "Acesso do suporte encerrado."; }); });

    private async Task Do(Func<Task> action)
    {
        Error = ""; Info = "";
        try { await action(); var keep = Info; await LoadAsync(); Info = keep; }
        catch (ApiException ex) { Error = "Não foi possível concluir: " + ex.Code; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }

    protected override async Task LoadCoreAsync()
    {
        var pre = await S.Api.GetAsync("/api/v1/support/preauthorization");
        var on = pre.GetProperty("operations").EnumerateArray().Select(x => x.GetString()).ToHashSet();
        Options.Clear();
        foreach (var a in pre.GetProperty("available").EnumerateArray())
            Options.Add(new SupportOption { Key = a.GetProperty("key").GetString()!, Name = a.GetProperty("name").GetString()!, Checked = on.Contains(a.GetProperty("key").GetString()) });

        Requests.Clear();
        foreach (var r in (await S.Api.GetAsync("/api/v1/support/elevation-requests")).EnumerateArray())
            Requests.Add(new SupportRequestRow(r.GetProperty("id").GetGuid(),
                $"{Ops(r.GetProperty("requestedOperations").GetString())} por {r.GetProperty("durationMinutes").GetInt32()} min — {r.GetProperty("reason").GetString()}",
                r.TryGetProperty("requestedAt", out var c) && c.ValueKind == JsonValueKind.String ? c.GetDateTimeOffset().LocalDateTime.ToString("dd/MM HH:mm") : ""));

        Sessions.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var s in (await S.Api.GetAsync("/api/v1/support/sessions")).EnumerateArray().Take(30))
        {
            var ended = s.GetProperty("endedAt").ValueKind != JsonValueKind.Null;
            var expires = s.GetProperty("expiresAt").GetDateTimeOffset();
            var active = !ended && expires > now;
            var elevated = s.GetProperty("mode").GetInt32() == 2 && active;
            Sessions.Add(new SupportSessionRow(s.GetProperty("id").GetGuid(),
                $"{s.GetProperty("startedAt").GetDateTimeOffset().LocalDateTime:dd/MM/yyyy HH:mm} — {s.GetProperty("reason").GetString()}",
                active ? (elevated ? $"ATIVA, pode alterar: {Ops(s.GetProperty("allowedOperations").GetString())} • até {expires.LocalDateTime:HH:mm}" : $"ATIVA, somente consulta • até {expires.LocalDateTime:HH:mm}")
                       : ended ? "Encerrada: " + s.GetProperty("endedReason").GetString() : "Expirada",
                active));
        }
        Raise(nameof(NoRequests));
    }
}
