using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

// ============================== Promoções e cupons ==============================

public sealed record PromoRow(Guid Id, string Name, string Kind, string Rule, string Period, bool Active, JsonElement Raw)
{
    public string Status => Active ? "Ativa" : "Desativada";
}

public sealed class PromotionsViewModel(ShellViewModel shell) : OwnerPage(shell, "Promoções", "Leve X pague Y, preço por quantidade, desconto percentual e cupons")
{
    public ObservableCollection<PromoRow> Items { get; } = [];
    private PromoRow? _selected;
    public PromoRow? Selected { get => _selected; set => Set(ref _selected, value); }
    private static readonly string[] Kinds = ["Leve X pague Y", "Preço por quantidade (atacarejo)", "Desconto percentual", "Cupom de desconto"];

    public RelayCommand NewCommand => RelayCommand.Sync(_ =>
    {
        var f = new FormViewModel { Title = "Nova promoção", Message = "Vale para as vendas do PDV; o servidor confere o cálculo em cada venda.", ConfirmText = "Criar promoção" };
        f.Fields.Add(new FormField("kind", "Tipo", Kinds[0], options: Kinds));
        f.Fields.Add(new FormField("name", "Nome (aparece no cupom)", "", Icons.Edit, "ex.: Leve 3 pague 2 refrigerante"));
        f.Fields.Add(new FormField("sku", "Código (SKU) ou código de barras do produto", "", Icons.Box, "vazio = todos os produtos (só percentual e cupom)"));
        f.Fields.Add(new FormField("a", "Leve X / quantidade mínima", "", Icons.Edit, "ex.: 3"));
        f.Fields.Add(new FormField("b", "Pague Y / preço unitário (R$) / percentual (%)", "", Icons.Edit, "ex.: 2 · 9,90 · 10"));
        f.Fields.Add(new FormField("coupon", "Código do cupom (só para cupom)", "", Icons.Edit, "ex.: VOLTE10"));
        f.Fields.Add(new FormField("days", "Válida por quantos dias (vazio = sem fim)", "", Icons.Edit, "ex.: 30"));
        Shell.ShowForm(f, async form =>
        {
            var type = Array.IndexOf(Kinds, form["kind"]) + 1;
            Guid? productId = null;
            if (form["sku"].Length > 0)
            {
                var hit = S.Db.SearchProducts(form["sku"], 5).FirstOrDefault(h => h.Sku == form["sku"] || h.Barcode == form["sku"]);
                if (hit.Id == Guid.Empty) { form.Error = "Produto não encontrado pelo código."; return false; }
                productId = hit.Id;
            }
            decimal.TryParse(form["a"], System.Globalization.NumberStyles.Number, AppServices.PtBr, out var a);
            decimal.TryParse(form["b"].Replace("%", ""), System.Globalization.NumberStyles.Number, AppServices.PtBr, out var b);
            var now = DateTimeOffset.Now;
            DateTimeOffset? ends = int.TryParse(form["days"], out var d) && d > 0 ? now.AddDays(d) : null;
            try
            {
                await S.Api.PostAsync("/api/v1/promotions", new
                {
                    name = form["name"], type, productId,
                    buyQty = type == 1 ? (int)a : 0, payQty = type == 1 ? (int)b : 0,
                    minQty = type == 2 ? a : 0m, unitPriceCents = type == 2 ? (long)Math.Round(b * 100) : 0L,
                    percent = type is 3 or 4 ? b : 0m, couponCode = type == 4 ? form["coupon"] : null, startsAt = now, endsAt = ends, active = true,
                });
                await LoadAsync();
                return true;
            }
            catch (ApiException ex) { form.Error = ex.Code is "invalid_promotion" or "coupon_taken" ? "Confira os dados: " + ex.Code : "Falha: " + ex.Code; }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => S.Session.Can("products.write"));

    public RelayCommand ToggleCommand => new(async _ =>
    {
        var r = Selected!.Raw;
        string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        try
        {
            await S.Api.PutAsync($"/api/v1/promotions/{Selected.Id}", new
            {
                name = r.GetProperty("name").GetString(), type = r.GetProperty("type").GetInt32(),
                productId = r.GetProperty("productId").ValueKind == JsonValueKind.Null ? (Guid?)null : r.GetProperty("productId").GetGuid(),
                buyQty = r.GetProperty("buyQty").GetInt32(), payQty = r.GetProperty("payQty").GetInt32(), minQty = r.GetProperty("minQty").GetDecimal(),
                unitPriceCents = r.GetProperty("unitPriceCents").GetInt64(), percent = r.GetProperty("percent").GetDecimal(), couponCode = Str("couponCode"),
                startsAt = r.GetProperty("startsAt").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : r.GetProperty("startsAt").GetDateTimeOffset(),
                endsAt = r.GetProperty("endsAt").ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : r.GetProperty("endsAt").GetDateTimeOffset(),
                active = !Selected.Active,
            });
            await LoadAsync();
        }
        catch (ApiException ex) { Error = "Falha: " + ex.Code; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }, _ => Selected is not null && S.Session.Can("products.write"));

    protected override async Task LoadCoreAsync()
    {
        Items.Clear();
        foreach (var p in (await S.Api.GetAsync("/api/v1/promotions")).EnumerateArray())
        {
            var type = p.GetProperty("type").GetInt32();
            var rule = type switch
            {
                1 => $"Leve {p.GetProperty("buyQty").GetInt32()} pague {p.GetProperty("payQty").GetInt32()}",
                2 => $"A partir de {p.GetProperty("minQty").GetDecimal():0.###} un: {AppServices.Money(p.GetProperty("unitPriceCents").GetInt64())} cada",
                3 => $"{p.GetProperty("percent").GetDecimal():0.##}% de desconto",
                _ => $"Cupom {p.GetProperty("couponCode").GetString()}: {p.GetProperty("percent").GetDecimal():0.##}%",
            };
            var ends = p.GetProperty("endsAt").ValueKind == JsonValueKind.Null ? "sem fim" : "até " + p.GetProperty("endsAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM/yyyy");
            Items.Add(new PromoRow(p.GetProperty("id").GetGuid(), p.GetProperty("name").GetString()!, Kinds[Math.Clamp(type - 1, 0, 3)], rule, ends, p.GetProperty("active").GetBoolean(), p.Clone()));
        }
        // Atualiza as regras usadas pelo caixa deste terminal.
        try { S.Db.Set("promotions", (await S.Api.GetAsync("/api/v1/promotions/active")).GetRawText()); } catch (ApiException) { }
    }
}

// ============================== Orçamentos ==============================

public sealed record QuoteRow(Guid Id, int Number, string Customer, string Total, string ValidUntil, string Status, bool IsOpen, List<(Guid ProductId, string Name, decimal Qty, long Unit)> Items);

public sealed class QuotesViewModel(ShellViewModel shell) : OwnerPage(shell, "Orçamentos", "Orçamentos salvos no caixa: abrir para vender, imprimir ou cancelar")
{
    public ObservableCollection<QuoteRow> Items { get; } = [];
    private QuoteRow? _selected;
    public QuoteRow? Selected { get => _selected; set => Set(ref _selected, value); }

    public RelayCommand OpenCommand => RelayCommand.Sync(_ =>
    {
        var q = Selected!;
        Shell.Pos.LoadQuote(q.Id, q.Items.Select(i => (i.ProductId, i.Name, i.Qty)));
        Shell.SelectedNav = Shell.Nav.First(n => n.Key == "pos");
    }, _ => Selected is { IsOpen: true });

    public RelayCommand PrintCommand => RelayCommand.Sync(_ =>
    {
        var q = Selected!;
        ReceiptPrinter.Print(new Receipt(DateTime.Now, q.Items.Select(i => new ReceiptLine(i.Name, i.Qty, i.Unit, (long)Math.Round(i.Unit * i.Qty))).ToList(),
            q.Items.Sum(i => (long)Math.Round(i.Unit * i.Qty)), 0, 0, $"ORÇAMENTO nº {q.Number} — válido até {q.ValidUntil}", q.Customer.Length > 0 ? q.Customer : null, Reference: $"Orçamento {q.Number}"));
    }, _ => Selected is not null);

    public RelayCommand CancelCommand => new(async _ =>
    {
        try { await S.Api.PostAsync($"/api/v1/quotes/{Selected!.Id}/cancel", new { }); await LoadAsync(); }
        catch (ApiException ex) { Error = "Falha: " + ex.Code; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }, _ => Selected is { IsOpen: true });

    protected override async Task LoadCoreAsync()
    {
        Items.Clear();
        foreach (var q in (await S.Api.GetAsync("/api/v1/quotes")).EnumerateArray())
        {
            var status = q.GetProperty("status").GetInt32();
            var valid = DateOnly.Parse(q.GetProperty("validUntil").GetString()!);
            var expired = status == 1 && valid < DateOnly.FromDateTime(DateTime.Today);
            Items.Add(new QuoteRow(q.GetProperty("id").GetGuid(), q.GetProperty("number").GetInt32(),
                q.TryGetProperty("customerName", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "",
                AppServices.Money(q.GetProperty("totalCents").GetInt64()), valid.ToString("dd/MM/yyyy"),
                status switch { 2 => "Virou venda", 3 => "Cancelado", _ => expired ? "Vencido" : "Em aberto" }, status == 1,
                q.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("productId").GetGuid(), i.GetProperty("productName").GetString()!,
                    i.GetProperty("quantity").GetDecimal(), i.GetProperty("unitPriceCents").GetInt64())).ToList()));
        }
    }
}
