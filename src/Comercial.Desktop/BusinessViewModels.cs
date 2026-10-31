using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Comercial.Desktop.Core;
using Comercial.Shared.Contracts;

namespace Comercial.Desktop;

// ============================== Compras ==============================

public sealed record SupplierRow(Guid Id, string Name, string Document, string Contact, string Phone, int LeadTimeDays);
public sealed record OrderRow(Guid Id, int Number, string Supplier, string Status, string StatusKey, string Created, int Items, string Total);
public sealed record ProductOption(Guid Id, string Name, long CostCents)
{
    public override string ToString() => Name;
}

public sealed class OrderLine(Guid itemId, string product, decimal ordered, decimal received, long costCents) : ObservableObject
{
    private string _receive = "", _cost = (costCents / 100m).ToString("0.00", AppServices.PtBr);
    public Guid ItemId { get; } = itemId;
    public string Product { get; } = product;
    public decimal Ordered { get; } = ordered;
    public decimal Received { get; } = received;
    public decimal Pending => Math.Max(0, Ordered - Received);
    public string OrderedText => Ordered.ToString("0.###", AppServices.PtBr);
    public string ReceivedText => Received.ToString("0.###", AppServices.PtBr);
    public string Cost => AppServices.Money(CostCents);
    public long CostCents { get; } = costCents;
    public string ReceiveQty { get => _receive; set => Set(ref _receive, value); }
    public string ReceiveCost { get => _cost; set => Set(ref _cost, value); }
}

public sealed class EditorLine(Guid productId, string name, decimal qty, long costCents) : ObservableObject
{
    private string _qty = qty.ToString("0.###", AppServices.PtBr), _cost = (costCents / 100m).ToString("0.00", AppServices.PtBr);
    public Guid ProductId { get; } = productId;
    public string Name { get; } = name;
    public string Qty { get => _qty; set => Set(ref _qty, value); }
    public string Cost { get => _cost; set => Set(ref _cost, value); }
}

public sealed class PurchasesViewModel : OwnerPage
{
    private OrderRow? _selected;
    private bool _editing, _showSuppliers;
    private SupplierRow? _editorSupplier;
    private ProductOption? _productToAdd;
    private string _detailTitle = "", _detailInfo = "";

    public PurchasesViewModel(ShellViewModel shell) : base(shell, "Compras", "Fornecedores, pedidos de compra, aprovação e recebimento")
    {
        NewSupplierCommand = RelayCommand.Sync(_ => SupplierForm(null), _ => S.Session.Can("suppliers.manage"));
        EditSupplierCommand = RelayCommand.Sync(p => SupplierForm(p as SupplierRow), _ => S.Session.Can("suppliers.manage"));
        NewOrderCommand = new RelayCommand(_ => StartEditorAsync(), _ => S.Session.Can("purchases.manage") && Suppliers.Count > 0);
        AddLineCommand = RelayCommand.Sync(_ => { if (ProductToAdd is { } p && EditorLines.All(l => l.ProductId != p.Id)) EditorLines.Add(new EditorLine(p.Id, p.Name, 1, p.CostCents)); });
        RemoveLineCommand = RelayCommand.Sync(p => { if (p is EditorLine l) EditorLines.Remove(l); });
        SaveOrderCommand = new RelayCommand(_ => SaveOrderAsync());
        CancelEditorCommand = RelayCommand.Sync(_ => Editing = false);
        ApproveCommand = new RelayCommand(_ => ActAsync("approve", null), _ => Selected?.StatusKey == "Draft" && S.Session.Can("purchases.approve"));
        ReceiveCommand = RelayCommand.Sync(_ => ReceiveForm(), _ => Selected?.StatusKey is "Approved" or "PartiallyReceived" && S.Session.Can("purchases.receive"));
        CancelOrderCommand = RelayCommand.Sync(_ => CancelOrder(), _ => Selected?.StatusKey is "Draft" or "Approved" && S.Session.Can("purchases.manage"));
        ShowOrdersCommand = RelayCommand.Sync(_ => ShowSuppliers = false);
        ShowSuppliersCommand = RelayCommand.Sync(_ => ShowSuppliers = true);
    }

    public ObservableCollection<SupplierRow> Suppliers { get; } = [];
    public ObservableCollection<OrderRow> Orders { get; } = [];
    public ObservableCollection<OrderLine> Lines { get; } = [];
    public ObservableCollection<EditorLine> EditorLines { get; } = [];
    public ObservableCollection<ProductOption> Products { get; } = [];
    public bool Editing { get => _editing; set { if (Set(ref _editing, value)) { Raise(nameof(Viewing)); } } }
    public bool Viewing => !Editing && Selected is not null;
    public bool ShowSuppliers { get => _showSuppliers; set { if (Set(ref _showSuppliers, value)) Raise(nameof(ShowOrders)); } }
    public bool ShowOrders => !ShowSuppliers;
    public SupplierRow? EditorSupplier { get => _editorSupplier; set => Set(ref _editorSupplier, value); }
    public ProductOption? ProductToAdd { get => _productToAdd; set => Set(ref _productToAdd, value); }
    public string DetailTitle { get => _detailTitle; set => Set(ref _detailTitle, value); }
    public string DetailInfo { get => _detailInfo; set => Set(ref _detailInfo, value); }
    public bool CanReceive => Selected?.StatusKey is "Approved" or "PartiallyReceived";
    public OrderRow? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Raise(nameof(Viewing)); Raise(nameof(CanReceive)); if (value is not null) { Editing = false; _ = LoadDetailAsync(value); } } }
    }
    public RelayCommand NewSupplierCommand { get; }
    public RelayCommand EditSupplierCommand { get; }
    public RelayCommand NewOrderCommand { get; }
    public RelayCommand AddLineCommand { get; }
    public RelayCommand RemoveLineCommand { get; }
    public RelayCommand SaveOrderCommand { get; }
    public RelayCommand CancelEditorCommand { get; }
    public RelayCommand ApproveCommand { get; }
    public RelayCommand ReceiveCommand { get; }
    public RelayCommand CancelOrderCommand { get; }
    public RelayCommand ShowOrdersCommand { get; }
    public RelayCommand ShowSuppliersCommand { get; }

    public static string StatusName(string key) => key switch
    {
        "Draft" => "Rascunho", "Approved" => "Aprovado", "PartiallyReceived" => "Recebido em parte", "Received" => "Recebido", "Cancelled" => "Cancelado", _ => key,
    };

    protected override async Task LoadCoreAsync()
    {
        Suppliers.Clear();
        foreach (var s in (await S.Api.GetAsync("/api/v1/suppliers")).EnumerateArray())
            Suppliers.Add(new SupplierRow(s.GetProperty("id").GetGuid(), s.GetProperty("name").GetString()!, Str(s, "document"), Str(s, "contactName"), Str(s, "phone"), s.GetProperty("leadTimeDays").GetInt32()));
        Orders.Clear();
        if (S.Session.Can("purchases.manage", "compras"))
            foreach (var o in (await S.Api.GetAsync("/api/v1/purchases")).EnumerateArray())
            {
                var st = o.GetProperty("status").GetString()!;
                Orders.Add(new OrderRow(o.GetProperty("id").GetGuid(), o.GetProperty("number").GetInt32(), o.GetProperty("supplierName").GetString()!, StatusName(st), st,
                    o.GetProperty("createdAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM/yyyy"), o.GetProperty("items").GetInt32(), AppServices.Money(o.GetProperty("totalCents").GetInt64())));
            }
        var keep = Selected?.Id;
        Selected = Orders.FirstOrDefault(o => o.Id == keep) ?? Orders.FirstOrDefault();
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private async Task LoadDetailAsync(OrderRow o)
    {
        try
        {
            var d = await S.Api.GetAsync($"/api/v1/purchases/{o.Id}");
            Lines.Clear();
            foreach (var i in d.GetProperty("po").GetProperty("items").EnumerateArray())
                Lines.Add(new OrderLine(i.GetProperty("id").GetGuid(), i.GetProperty("productName").GetString()!, i.GetProperty("quantityOrdered").GetDecimal(),
                    i.GetProperty("quantityReceived").GetDecimal(), i.GetProperty("unitCostCents").GetInt64()));
            foreach (var l in Lines) l.ReceiveQty = l.Pending > 0 ? l.Pending.ToString("0.###", AppServices.PtBr) : "";
            DetailTitle = $"Pedido #{o.Number} — {o.Supplier}";
            var receipts = d.GetProperty("receipts").GetArrayLength();
            DetailInfo = $"{o.Status} • criado em {o.Created} • total {o.Total}" + (receipts > 0 ? $" • {receipts} recebimento(s)" : "");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { DetailInfo = "Não foi possível carregar o pedido."; }
    }

    private void SupplierForm(SupplierRow? row)
    {
        var f = new FormViewModel { Title = row is null ? "Novo fornecedor" : "Editar fornecedor", ConfirmText = "Salvar" };
        f.Fields.Add(new FormField("name", "Nome / razão social", row?.Name ?? "", Icons.Bank));
        f.Fields.Add(new FormField("document", "CNPJ ou CPF", row?.Document ?? "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("contact", "Pessoa de contato", row?.Contact ?? "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("phone", "Telefone", row?.Phone ?? "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("lead", "Prazo de entrega (dias)", row?.LeadTimeDays.ToString() ?? "7", Icons.Calendar));
        Shell.ShowForm(f, async form =>
        {
            if (form["name"].Length == 0) { form.Error = "Informe o nome."; return false; }
            var body = new { name = form["name"], document = Nul(form["document"]), contactName = Nul(form["contact"]), phone = Nul(form["phone"]), leadTimeDays = int.TryParse(form["lead"], out var d) ? d : 0 };
            if (row is null) await S.Api.PostAsync("/api/v1/suppliers", body); else await S.Api.PutAsync($"/api/v1/suppliers/{row.Id}", body);
            await LoadAsync();
            return true;
        });
    }

    private static string? Nul(string s) => s.Length == 0 ? null : s;

    /// <summary>Abre o editor de pedido já com a sugestão de reposição (produtos no mínimo).</summary>
    private async Task StartEditorAsync()
    {
        EditorLines.Clear(); Products.Clear();
        try
        {
            foreach (var p in (await S.Api.GetAsync($"/api/v1/stock/balances?storeId={S.Settings.StoreId}")).EnumerateArray())
                Products.Add(new ProductOption(p.GetProperty("productId").GetGuid(), p.GetProperty("name").GetString()!, p.GetProperty("costCents").GetInt64()));
            foreach (var s in (await S.Api.GetAsync($"/api/v1/purchases/suggestions?storeId={S.Settings.StoreId}")).EnumerateArray())
                EditorLines.Add(new EditorLine(s.GetProperty("productId").GetGuid(), s.GetProperty("name").GetString()!, s.GetProperty("suggested").GetDecimal(), s.GetProperty("unitCostCents").GetInt64()));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { Error = "Não foi possível carregar produtos."; return; }
        EditorSupplier = Suppliers.FirstOrDefault();
        Editing = true;
    }

    private async Task SaveOrderAsync()
    {
        if (EditorSupplier is null) { Error = "Escolha o fornecedor."; return; }
        var items = new List<object>();
        foreach (var l in EditorLines)
        {
            if (!decimal.TryParse(l.Qty, System.Globalization.NumberStyles.Number, AppServices.PtBr, out var q) || q <= 0 || !PromptViewModel.TryMoney(l.Cost, out var c))
            { Error = $"Quantidade ou custo inválido em {l.Name}."; return; }
            items.Add(new { productId = l.ProductId, quantity = q, unitCostCents = c });
        }
        if (items.Count == 0) { Error = "Adicione ao menos um produto."; return; }
        try
        {
            await S.Api.PostAsync("/api/v1/purchases", new { supplierId = EditorSupplier.Id, storeId = S.Settings.StoreId, items });
            Editing = false;
            await LoadAsync();
        }
        catch (ApiException ex) { Error = "Não foi possível salvar o pedido: " + ex.Code; }
    }

    private async Task ActAsync(string action, object? body)
    {
        try { await S.Api.PostAsync($"/api/v1/purchases/{Selected!.Id}/{action}", body ?? new { }); await LoadAsync(); }
        catch (ApiException ex) { Error = ex.Code == "permission_denied" ? "Sem permissão para esta ação." : "Falha: " + ex.Code; }
    }

    private void CancelOrder() => Shell.ShowPrompt(new PromptViewModel { Title = $"Cancelar pedido #{Selected!.Number}", Message = "O pedido fica no histórico como cancelado.", TextLabel = "Motivo", ConfirmText = "Cancelar pedido" },
        async p =>
        {
            if (p.Text.Trim().Length < 5) { p.Error = "Informe o motivo."; return false; }
            await ActAsync("cancel", new { reason = p.Text.Trim() });
            return true;
        });

    private void ReceiveForm()
    {
        var f = new FormViewModel
        {
            Title = $"Receber pedido #{Selected!.Number}",
            Message = "Confira as quantidades e custos na tabela do pedido antes de confirmar. Diferenças de custo ficam registradas como divergência.",
            ConfirmText = "Confirmar recebimento",
        };
        f.Fields.Add(new FormField("invoice", "Número da nota fiscal", "", Icons.Edit, "opcional"));
        if (S.Session.Modules.Contains("contas")) f.Fields.Add(new FormField("due", "Gerar conta a pagar com vencimento em (dias)", "30", Icons.Calendar, "deixe vazio para não gerar"));
        Shell.ShowForm(f, async form =>
        {
            var items = new List<object>();
            foreach (var l in Lines.Where(l => l.ReceiveQty.Trim().Length > 0))
            {
                if (!decimal.TryParse(l.ReceiveQty, System.Globalization.NumberStyles.Number, AppServices.PtBr, out var q) || q < 0 || !PromptViewModel.TryMoney(l.ReceiveCost, out var c))
                { form.Error = $"Quantidade ou custo inválido em {l.Product}."; return false; }
                if (q > 0) items.Add(new { purchaseItemId = l.ItemId, quantity = q, unitCostCents = c });
            }
            if (items.Count == 0) { form.Error = "Informe ao menos uma quantidade recebida."; return false; }
            int? due = Fields(form, "due") is { Length: > 0 } d && int.TryParse(d, out var days) ? days : null;
            try
            {
                await S.Api.PostAsync($"/api/v1/purchases/{Selected!.Id}/receive", new { items, invoiceNumber = Nul(form["invoice"]), payableDueInDays = due, acceptExcess = false });
            }
            catch (ApiException ex) when (ex.Code == "excess_quantity")
            {
                form.Error = "Há quantidade maior que a pendente. Corrija na tabela (excesso precisa de conferência).";
                return false;
            }
            await LoadAsync();
            return true;
        });
    }

    private static string Fields(FormViewModel f, string key) => f.Fields.FirstOrDefault(x => x.Key == key)?.Value.Trim() ?? "";
}

// ============================== Clientes ==============================

public sealed record CustomerRow(Guid Id, string Name, string Document, string Phone, string Email, bool Consent);
public sealed record StatementRow(string When, string Type, string Points, string Reason, bool Positive);

public sealed class CustomersViewModel : OwnerPage
{
    private string _query = "", _detailTitle = "", _detailInfo = "", _points = "";
    private CustomerRow? _selected;

    public CustomersViewModel(ShellViewModel shell) : base(shell, "Clientes", "Cadastro, histórico de compras, pontos de fidelidade e privacidade (LGPD)")
    {
        SearchCommand = new RelayCommand(_ => LoadAsync());
        NewCommand = RelayCommand.Sync(_ => CustomerForm(null), _ => S.Session.Can("customers.manage"));
        EditCommand = RelayCommand.Sync(_ => CustomerForm(Selected), _ => Selected is not null && S.Session.Can("customers.manage"));
        ExportCommand = new RelayCommand(_ => ExportAsync(), _ => Selected is not null && S.Session.Can("customers.privacy"));
        AnonymizeCommand = RelayCommand.Sync(_ => Anonymize(), _ => Selected is not null && S.Session.Can("customers.privacy"));
        ProgramCommand = new RelayCommand(_ => ProgramFormAsync(), _ => S.Session.Can("loyalty.manage", "fidelidade"));
    }

    public ObservableCollection<CustomerRow> Customers { get; } = [];
    public ObservableCollection<StatementRow> Statement { get; } = [];
    public ObservableCollection<SaleRow> Recent { get; } = [];
    public string Query { get => _query; set => Set(ref _query, value); }
    public string DetailTitle { get => _detailTitle; set => Set(ref _detailTitle, value); }
    public string DetailInfo { get => _detailInfo; set => Set(ref _detailInfo, value); }
    public string PointsText { get => _points; set => Set(ref _points, value); }
    private string _credit = "";
    public string CreditText { get => _credit; set => Set(ref _credit, value); }
    public RelayCommand LimitCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Limite do fiado: " + Selected!.Name, Message = "Valor máximo que o cliente pode dever. 0 = não pode comprar fiado.", ValueLabel = "Limite (R$)", ConfirmText = "Salvar limite",
    }, async p =>
    {
        if (!PromptViewModel.TryMoney(p.Value, out var cents)) { p.Error = "Valor inválido."; return false; }
        try { await S.Api.PutAsync($"/api/v1/customers/{Selected!.Id}/credit-limit", new { limitCents = cents }); await LoadDetailAsync(Selected!); return true; }
        catch (ApiException ex) { p.Error = ex.Status == 403 ? "Só o dono/financeiro define o limite." : "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => Selected is not null && S.Session.Can("finance.manage", "contas"));
    private static readonly string[] ReceiveMethods = ["Dinheiro", "Cartão de débito", "Cartão de crédito", "PIX"];
    public RelayCommand ReceiveCreditCommand => RelayCommand.Sync(_ =>
    {
        var f = new FormViewModel { Title = "Receber fiado: " + Selected!.Name, Message = "Baixa as contas mais antigas primeiro. Em dinheiro, o valor entra no seu caixa aberto.", ConfirmText = "Receber" };
        f.Fields.Add(new FormField("amount", "Valor recebido (R$)", "", Icons.Money));
        f.Fields.Add(new FormField("method", "Forma", ReceiveMethods[0], options: ReceiveMethods));
        Shell.ShowForm(f, async form =>
        {
            if (!PromptViewModel.TryMoney(form["amount"], out var cents) || cents <= 0) { form.Error = "Valor inválido."; return false; }
            try
            {
                await S.Api.PostAsync($"/api/v1/customers/{Selected!.Id}/credit/receive", new { amountCents = cents, method = Array.IndexOf(ReceiveMethods, form["method"]) + 1 });
                await LoadDetailAsync(Selected!);
                if (form["method"] == ReceiveMethods[0]) ReceiptPrinter.OpenDrawer();
                return true;
            }
            catch (ApiException ex) { form.Error = ex.Code switch { "overpayment" => "Valor maior que a dívida.", "no_open_cash" => "Abra o caixa para receber em dinheiro.", _ => "Falha: " + ex.Code }; }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => Selected is not null && S.Session.Can("cash.operate", "contas"));
    // ---------- Cobrança do fiado pelo WhatsApp da loja ----------
    private string _billing = "";
    private int? _billingDay;
    /// <summary>Quadro "melhor dia para cobrar": aparece para quem vê o financeiro (módulo Contas).</summary>
    public bool CanBilling => S.Session.Can("finance.read", "contas");
    public string BillingText { get => _billing; set => Set(ref _billing, value); }

    public RelayCommand BillingDayCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Dia de cobrança: " + Selected!.Name,
        Message = "Todo mês, nesse dia, o WhatsApp da loja manda para o cliente quanto ele deve e o que comprou em cada dia. Deixe vazio para não cobrar automaticamente.",
        ValueLabel = "Dia do mês (1 a 31)", Value = _billingDay?.ToString() ?? "", ConfirmText = "Salvar dia",
    }, async p =>
    {
        int? day = null;
        if (p.Value.Trim().Length > 0)
        {
            if (!int.TryParse(p.Value.Trim(), out var d) || d is < 1 or > 31) { p.Error = "Use um dia de 1 a 31 (ou deixe vazio)."; return false; }
            day = d;
        }
        try { await S.Api.PutAsync($"/api/v1/customers/{Selected!.Id}/billing-day", new { day }); await LoadDetailAsync(Selected!); return true; }
        catch (ApiException ex) { p.Error = ex.Status == 403 ? "Só o dono/financeiro define o dia de cobrança." : "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => Selected is not null && S.Session.Can("finance.manage", "contas"));

    /// <summary>Botão verde: mostra a mensagem com o saldo e as compras por dia e envia pelo WhatsApp da loja.</summary>
    public RelayCommand ChargeWhatsAppCommand => new(async _ =>
    {
        var c = Selected!;
        try
        {
            var pv = await S.Api.GetAsync($"/api/v1/customers/{c.Id}/charge-preview");
            if (!pv.GetProperty("canSend").GetBoolean())
            {
                Shell.ShowPrompt(new PromptViewModel { Title = "Cobrar pelo WhatsApp", Message = pv.GetProperty("reason").GetString()!, ConfirmText = "Entendi" }, _ => Task.FromResult(true));
                return;
            }
            Shell.ShowPrompt(new PromptViewModel
            {
                Title = "Cobrar pelo WhatsApp: " + c.Name,
                Message = pv.GetProperty("text").GetString() + $"\n\nEnviar agora para o celular {pv.GetProperty("phone").GetString()}?",
                ConfirmText = "Enviar cobrança",
            }, async p =>
            {
                try { await S.Api.PostAsync($"/api/v1/customers/{c.Id}/charge", new { }); await LoadDetailAsync(c); return true; }
                catch (ApiException ex) { p.Error = ex.Code is "charge_too_soon" or "whatsapp_send_failed" or "nothing_to_charge" or "invalid_phone" or "whatsapp_not_connected" ? ex.Message : "Falha: " + ex.Code; }
                catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
                return false;
            });
        }
        catch (ApiException ex) { DetailInfo = "Não foi possível preparar a cobrança: " + ex.Code; }
        catch (HttpRequestException) { DetailInfo = "Sem conexão com o servidor."; }
    }, _ => Selected is not null && S.Session.Can("finance.manage", "contas"));

    /// <summary>Usado só pelas capturas de tela.</summary>
    public void SetBillingForPreview(int? day, string? last)
    {
        _billingDay = day;
        BillingText = BillingLabel(day, last);
    }

    private static string BillingLabel(int? day, string? last) =>
        (day is null ? "Cobrança automática desligada" : $"Cobrança automática: todo dia {day}") + (last is null ? "" : $"\nÚltima: {last}");

    public bool HasSelection => Selected is not null;
    public CustomerRow? Selected { get => _selected; set { if (Set(ref _selected, value)) { Raise(nameof(HasSelection)); if (value is not null) _ = LoadDetailAsync(value); } } }
    public RelayCommand SearchCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand AnonymizeCommand { get; }
    public RelayCommand ProgramCommand { get; }

    protected override async Task LoadCoreAsync()
    {
        Customers.Clear();
        foreach (var c in (await S.Api.GetAsync("/api/v1/customers" + (Query.Trim().Length > 0 ? "?q=" + Uri.EscapeDataString(Query.Trim()) : ""))).EnumerateArray())
            Customers.Add(new CustomerRow(c.GetProperty("id").GetGuid(), c.GetProperty("name").GetString()!, Str(c, "document"), Str(c, "phone"), Str(c, "email"), c.GetProperty("marketingConsent").GetBoolean()));
        Selected = Customers.FirstOrDefault();
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private async Task LoadDetailAsync(CustomerRow c)
    {
        try
        {
            var d = await S.Api.GetAsync($"/api/v1/customers/{c.Id}");
            DetailTitle = c.Name;
            DetailInfo = $"{d.GetProperty("purchases").GetInt32()} compra(s) • total {AppServices.Money(d.GetProperty("totalSpentCents").GetInt64())}"
                         + (c.Consent ? " • aceita receber ofertas" : " • não aceita ofertas");
            Recent.Clear();
            foreach (var s in d.GetProperty("recent").EnumerateArray())
                Recent.Add(new SaleRow(s.GetProperty("id").GetGuid(), s.GetProperty("receivedAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM/yyyy HH:mm"), "", "", "",
                    AppServices.Money(s.GetProperty("totalCents").GetInt64()), s.GetProperty("status").GetString() == "Cancelled" ? "Cancelada" : "Concluída", false, false));
            Statement.Clear();
            PointsText = "";
            CreditText = "";
            var cust = d.GetProperty("customer");
            _billingDay = cust.TryGetProperty("billingDay", out var bd) && bd.ValueKind == JsonValueKind.Number ? bd.GetInt32() : null;
            var lastCharge = cust.TryGetProperty("lastChargeSentOn", out var lc) && lc.ValueKind == JsonValueKind.String
                ? DateOnly.Parse(lc.GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToString("dd/MM/yyyy") : null;
            BillingText = BillingLabel(_billingDay, lastCharge);
            if (S.Session.Can("customers.read", "contas"))
            {
                try
                {
                    var cr = await S.Api.GetAsync($"/api/v1/customers/{c.Id}/credit");
                    var limit = cr.GetProperty("limitCents").GetInt64();
                    CreditText = limit > 0 || cr.GetProperty("openCents").GetInt64() > 0
                        ? $"Fiado: deve {AppServices.Money(cr.GetProperty("openCents").GetInt64())} • limite {AppServices.Money(limit)} • disponível {AppServices.Money(cr.GetProperty("availableCents").GetInt64())}"
                        : "Fiado: sem limite (não pode comprar fiado)";
                }
                catch (ApiException) { }
            }
            if (d.GetProperty("loyalty") is { ValueKind: JsonValueKind.Object } bal)
            {
                PointsText = $"{bal.GetProperty("available").GetInt32()} ponto(s) disponíveis";
                var st = await S.Api.GetAsync($"/api/v1/customers/{c.Id}/loyalty");
                foreach (var t in st.GetProperty("statement").EnumerateArray())
                {
                    var pts = t.GetProperty("points").GetInt32();
                    Statement.Add(new StatementRow(t.GetProperty("createdAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM HH:mm"), TypeName(t.GetProperty("type").GetString()!),
                        (pts > 0 ? "+" : "") + pts, Str(t, "reason"), pts > 0));
                }
            }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { DetailInfo = "Não foi possível carregar o cliente."; }
    }

    private static string TypeName(string t) => t switch
    {
        "Earn" => "Compra", "Redeem" => "Resgate", "EarnReversal" => "Estorno de pontos", "RedeemRefund" => "Pontos devolvidos", _ => "Ajuste",
    };

    private void CustomerForm(CustomerRow? row)
    {
        var f = new FormViewModel { Title = row is null ? "Novo cliente" : "Editar cliente", Message = "Colete só o necessário. O consentimento para ofertas fica registrado com a data.", ConfirmText = "Salvar" };
        f.Fields.Add(new FormField("name", "Nome", row?.Name ?? "", Icons.Edit));
        f.Fields.Add(new FormField("document", "CPF", row?.Document ?? "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("phone", "Telefone", row?.Phone ?? "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("email", "E-mail", row?.Email ?? "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("consent", "Aceita receber ofertas?", row?.Consent == true ? "Sim" : "Não", options: ["Sim", "Não"]));
        Shell.ShowForm(f, async form =>
        {
            if (form["name"].Length == 0) { form.Error = "Informe o nome."; return false; }
            var body = new { name = form["name"], document = form["document"].Length == 0 ? null : form["document"], phone = form["phone"], email = form["email"].Length == 0 ? null : form["email"], marketingConsent = form["consent"] == "Sim" };
            try
            {
                if (row is null) await S.Api.PostAsync("/api/v1/customers", body); else await S.Api.PutAsync($"/api/v1/customers/{row.Id}", body);
            }
            catch (ApiException ex) when (ex.Code is "document_taken" or "invalid_document" or "invalid_email")
            {
                form.Error = ex.Code switch { "document_taken" => "Já existe cliente com este CPF.", "invalid_document" => "CPF deve ter 11 dígitos.", _ => "E-mail inválido." };
                return false;
            }
            await LoadAsync();
            return true;
        });
    }

    private async Task ExportAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"dados-cliente-{DateTime.Now:yyyyMMdd}.json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        var data = await S.Api.GetAsync($"/api/v1/customers/{Selected!.Id}/export");
        await System.IO.File.WriteAllTextAsync(dlg.FileName, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Anonymize() => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Anonimizar cliente", Message = "Remove nome, CPF, telefone e e-mail de forma definitiva. As vendas continuam no histórico, sem identificar a pessoa.",
        TextLabel = "Motivo (ex.: pedido do titular)", ConfirmText = "Anonimizar",
    }, async p =>
    {
        if (p.Text.Trim().Length < 5) { p.Error = "Informe o motivo."; return false; }
        await S.Api.PostAsync($"/api/v1/customers/{Selected!.Id}/anonymize", new { reason = p.Text.Trim() });
        await LoadAsync();
        return true;
    });

    private async Task ProgramFormAsync()
    {
        var p = await S.Api.GetAsync("/api/v1/loyalty/program");
        var f = new FormViewModel { Title = "Programa de fidelidade", Message = "Regras valem para as próximas vendas.", ConfirmText = "Salvar regras" };
        f.Fields.Add(new FormField("enabled", "Programa ativo?", p.GetProperty("enabled").GetBoolean() ? "Sim" : "Não", options: ["Sim", "Não"]));
        f.Fields.Add(new FormField("ppr", "Pontos por R$ 1,00 gasto", p.GetProperty("pointsPerReal").GetInt32().ToString(), Icons.Add));
        f.Fields.Add(new FormField("cpp", "Valor de cada ponto no resgate (centavos)", p.GetProperty("centsPerPoint").GetInt32().ToString(), Icons.Money));
        f.Fields.Add(new FormField("min", "Mínimo de pontos para resgatar", p.GetProperty("minPointsToRedeem").GetInt32().ToString(), Icons.Add));
        f.Fields.Add(new FormField("exp", "Validade dos pontos (dias, 0 = não vencem)", p.GetProperty("expirationDays").GetInt32().ToString(), Icons.Calendar));
        f.Fields.Add(new FormField("max", "Máximo da compra pago com pontos (%)", p.GetProperty("maxRedeemPercent").GetInt32().ToString(), Icons.Chart));
        Shell.ShowForm(f, async form =>
        {
            int I(string k) => int.TryParse(form[k], out var v) ? v : -1;
            await S.Api.PutAsync("/api/v1/loyalty/program", new { enabled = form["enabled"] == "Sim", pointsPerReal = I("ppr"), centsPerPoint = I("cpp"), minPointsToRedeem = I("min"), expirationDays = I("exp"), maxRedeemPercent = I("max") });
            return true;
        });
    }
}

// ============================== Financeiro ==============================

public sealed record TitleRow(Guid Id, string Kind, string Description, string Category, string Counterparty, string Due, string Amount, string Status, bool Overdue, bool IsOpen, bool IsPaid);
public sealed record ChoiceOption(string Key, string Title);

public sealed class FinanceViewModel : OwnerPage
{
    private ChoiceOption _kind, _window;
    private TitleRow? _selected;

    public FinanceViewModel(ShellViewModel shell) : base(shell, "Financeiro", "Contas a pagar e a receber, despesas e fluxo de caixa")
    {
        _kind = Kinds[0]; _window = Windows[0];
        NewTitleCommand = RelayCommand.Sync(_ => TitleForm(), _ => S.Session.Can("finance.manage", "contas"));
        NewExpenseCommand = RelayCommand.Sync(_ => ExpenseForm(), _ => S.Session.Can("finance.manage", "despesas"));
        PayCommand = RelayCommand.Sync(_ => Pay(), _ => Selected?.IsOpen == true && S.Session.Can("finance.manage"));
        CancelCommand = RelayCommand.Sync(_ => Reason("cancel", "Cancelar título", "Cancelar"), _ => Selected?.IsOpen == true && S.Session.Can("finance.manage"));
        ReopenCommand = RelayCommand.Sync(_ => Reason("reopen", "Estornar baixa", "Estornar"), _ => Selected?.IsPaid == true && S.Session.Can("finance.manage"));
    }

    public IReadOnlyList<ChoiceOption> Kinds { get; } = [new("0", "Todos"), new("1", "A pagar"), new("2", "A receber")];
    public IReadOnlyList<ChoiceOption> Windows { get; } = [new("month", "Este mês"), new("next30", "Próximos 30 dias"), new("overdue", "Vencidos"), new("open", "Todos em aberto")];
    public ChoiceOption Kind { get => _kind; set { if (Set(ref _kind, value)) _ = LoadAsync(); } }
    public ChoiceOption Window { get => _window; set { if (Set(ref _window, value)) _ = LoadAsync(); } }
    public ObservableCollection<Kpi> Kpis { get; } = [];
    public ObservableCollection<TitleRow> Titles { get; } = [];
    public TitleRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public RelayCommand NewTitleCommand { get; }
    public RelayCommand NewExpenseCommand { get; }
    public RelayCommand PayCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ReopenCommand { get; }

    protected override async Task LoadCoreAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        Kpis.Clear();
        if (S.Session.Modules.Contains("financeiro"))
        {
            var s = await S.Api.GetAsync($"/api/v1/finance/summary?from={monthStart:yyyy-MM-dd}&to={monthEnd:yyyy-MM-dd}");
            Kpis.Add(new Kpi("A pagar no mês", AppServices.Money(s.GetProperty("openPayableCents").GetInt64()), "em aberto", Icons.Calendar, "warn"));
            Kpis.Add(new Kpi("A receber no mês", AppServices.Money(s.GetProperty("openReceivableCents").GetInt64()), "em aberto", Icons.Money, "accent"));
            Kpis.Add(new Kpi("Vencidos", AppServices.Money(s.GetProperty("overduePayableCents").GetInt64() + s.GetProperty("overdueReceivableCents").GetInt64()),
                $"{s.GetProperty("overdueCount").GetInt32()} título(s)", Icons.History, s.GetProperty("overdueCount").GetInt32() > 0 ? "warn" : "neutral"));
            Kpis.Add(new Kpi("Pago no mês", AppServices.Money(s.GetProperty("paidCents").GetInt64()), $"recebido {AppServices.Money(s.GetProperty("receivedCents").GetInt64())}", Icons.Chart, "primary"));
        }
        var q = new List<string>();
        if (Kind.Key != "0") q.Add("kind=" + Kind.Key);
        switch (Window.Key)
        {
            case "month": q.Add($"from={monthStart:yyyy-MM-dd}"); q.Add($"to={monthEnd:yyyy-MM-dd}"); break;
            case "next30": q.Add($"from={today:yyyy-MM-dd}"); q.Add($"to={today.AddDays(30):yyyy-MM-dd}"); q.Add("status=1"); break;
            case "overdue": q.Add($"to={today.AddDays(-1):yyyy-MM-dd}"); q.Add("status=1"); break;
            default: q.Add("status=1"); break;
        }
        Titles.Clear();
        foreach (var t in (await S.Api.GetAsync("/api/v1/finance/titles?" + string.Join('&', q))).EnumerateArray())
        {
            var due = DateOnly.Parse(t.GetProperty("dueDate").GetString()!);
            var status = t.GetProperty("status").GetInt32();
            Titles.Add(new TitleRow(t.GetProperty("id").GetGuid(), t.GetProperty("kind").GetInt32() == 1 ? "Pagar" : "Receber", t.GetProperty("description").GetString()!,
                t.GetProperty("category").GetString()!, t.TryGetProperty("counterparty", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "",
                due.ToString("dd/MM/yyyy"), AppServices.Money(t.GetProperty("amountCents").GetInt64()),
                status switch { 1 => "Em aberto", 2 => "Pago", _ => "Cancelado" }, status == 1 && due < today, status == 1, status == 2));
        }
    }

    private void TitleForm()
    {
        var f = new FormViewModel { Title = "Nova conta", Message = "Parcelas dividem o valor total; recorrência repete o valor todo mês.", ConfirmText = "Lançar" };
        f.Fields.Add(new FormField("kind", "Tipo", "A pagar", options: ["A pagar", "A receber"]));
        f.Fields.Add(new FormField("description", "Descrição", "", Icons.Edit));
        f.Fields.Add(new FormField("category", "Categoria", "Outras despesas", options: [.. new[] { "Compra de mercadoria", "Vendas a prazo" }, "Aluguel", "Energia", "Água", "Internet/telefone", "Salários", "Impostos", "Taxas de cartão", "Manutenção", "Marketing", "Outras despesas"]));
        f.Fields.Add(new FormField("counterparty", "Fornecedor / cliente", "", Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("due", "Primeiro vencimento (dd/mm/aaaa)", DateTime.Today.AddDays(30).ToString("dd/MM/yyyy"), Icons.Calendar));
        f.Fields.Add(new FormField("total", "Valor total (R$)", "", Icons.Money));
        f.Fields.Add(new FormField("installments", "Parcelas", "1", Icons.Add));
        f.Fields.Add(new FormField("recurrence", "Ou recorrência mensal (meses)", "0", Icons.Calendar));
        Shell.ShowForm(f, async form =>
        {
            if (!DateOnly.TryParseExact(form["due"], "dd/MM/yyyy", out var due)) { form.Error = "Data de vencimento inválida."; return false; }
            if (!PromptViewModel.TryMoney(form["total"], out var total) || total <= 0) { form.Error = "Informe o valor."; return false; }
            await S.Api.PostAsync("/api/v1/finance/titles", new
            {
                kind = form["kind"] == "A pagar" ? 1 : 2, description = form["description"], category = form["category"], counterparty = form["counterparty"],
                firstDueDate = due, totalCents = total, installments = int.TryParse(form["installments"], out var i) ? i : 1, recurrenceMonths = int.TryParse(form["recurrence"], out var r) ? r : 0,
            });
            await LoadAsync();
            return true;
        });
    }

    private void ExpenseForm()
    {
        var f = new FormViewModel { Title = "Nova despesa", Message = "Despesa paga (luz, aluguel, taxas...). Entra no resultado líquido estimado.", ConfirmText = "Lançar despesa" };
        f.Fields.Add(new FormField("description", "Descrição", "", Icons.Edit));
        f.Fields.Add(new FormField("category", "Categoria", "Energia", options: ["Aluguel", "Energia", "Água", "Internet/telefone", "Salários", "Impostos", "Taxas de cartão", "Manutenção", "Marketing", "Outras despesas"]));
        f.Fields.Add(new FormField("amount", "Valor (R$)", "", Icons.Money));
        f.Fields.Add(new FormField("paid", "Pago em (dd/mm/aaaa)", DateTime.Today.ToString("dd/MM/yyyy"), Icons.Calendar));
        f.Fields.Add(new FormField("method", "Forma de pagamento", "PIX", options: ["PIX", "Boleto", "Dinheiro", "Cartão", "Transferência"]));
        Shell.ShowForm(f, async form =>
        {
            if (!DateOnly.TryParseExact(form["paid"], "dd/MM/yyyy", out var paid)) { form.Error = "Data inválida."; return false; }
            if (!PromptViewModel.TryMoney(form["amount"], out var amount) || amount <= 0) { form.Error = "Informe o valor."; return false; }
            if (form["description"].Length == 0) { form.Error = "Informe a descrição."; return false; }
            await S.Api.PostAsync("/api/v1/finance/expenses", new { description = form["description"], category = form["category"], amountCents = amount, paidOn = paid, method = form["method"] });
            await LoadAsync();
            return true;
        });
    }

    private void Pay() => Shell.ShowPrompt(new PromptViewModel
    {
        Title = Selected!.Kind == "Pagar" ? "Registrar pagamento" : "Registrar recebimento", Message = $"{Selected.Description} — {Selected.Amount}",
        ValueLabel = "Valor (deixe vazio para o valor total)", TextLabel = "Forma (PIX, boleto...)", ConfirmText = "Confirmar",
    }, async p =>
    {
        if (!PromptViewModel.TryMoney(p.Value, out var cents)) { p.Error = "Valor inválido."; return false; }
        await S.Api.PostAsync($"/api/v1/finance/titles/{Selected!.Id}/pay", new { amountCents = cents == 0 ? (long?)null : cents, method = p.Text.Trim() });
        await LoadAsync();
        return true;
    });

    private void Reason(string action, string title, string confirm) => Shell.ShowPrompt(new PromptViewModel { Title = title, Message = Selected!.Description, TextLabel = "Motivo", ConfirmText = confirm },
        async p =>
        {
            if (p.Text.Trim().Length < 5) { p.Error = "Informe o motivo."; return false; }
            await S.Api.PostAsync($"/api/v1/finance/titles/{Selected!.Id}/{action}", new { reason = p.Text.Trim() });
            await LoadAsync();
            return true;
        });
}

// ============================== Pendências (vendas offline recusadas) ==============================

public sealed record PendingRow(string Key, string Type, string When, string Total, long TotalCents, string Reason, string ErrorCode, string PayloadJson, bool CanResend);

/// <summary>
/// Conciliação: vendas feitas offline que o servidor recusou (ex.: preço mudou, caixa já fechado).
/// O gerente reenvia com o preço atual (o que o cliente pagou vira o total; a diferença vira desconto) ou descarta com motivo.
/// </summary>
public sealed class PendingViewModel(ShellViewModel shell) : OwnerPage(shell, "Pendências", "Vendas offline recusadas pelo servidor aguardando decisão do gerente")
{
    public ObservableCollection<PendingRow> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public RelayCommand ResendCommand => RelayCommand.Sync(p => { if (p is PendingRow r) Resend(r); });
    public RelayCommand DiscardCommand => RelayCommand.Sync(p => { if (p is PendingRow r) Discard(r); });

    public static string Reason(string? code) => code switch
    {
        "price_mismatch" => "Preço mudou no cadastro depois da venda",
        "cash_not_open" => "Caixa já estava fechado no servidor",
        "not_owner" => "Caixa pertence a outro operador",
        "product_not_found" => "Produto removido do catálogo",
        "store_not_found" => "Loja não existe mais no servidor (dado de instalação/empresa anterior)",
        "cash_session_not_found" => "Caixa não existe mais no servidor",
        "insufficient_points" => "Cliente sem pontos suficientes",
        "clock_in_future" => "Relógio do computador adiantado",
        "license_restricted" => "Licença restrita na data da venda",
        _ => code ?? "Recusada",
    };

    protected override Task LoadCoreAsync()
    {
        Items.Clear();
        foreach (var i in S.Db.All().Where(i => i.Status == OutboxStatus.Rejected).OrderByDescending(i => i.LocalTime))
        {
            long total = 0;
            if (i.Type == SyncTypes.SaleCreate && JsonSerializer.Deserialize<CreateSaleRequest>(i.PayloadJson, ApiClient.Json) is { } sale)
                total = sale.Items.Sum(x => (long)Math.Round((x.ClientUnitPriceCents ?? 0) * x.Quantity) - x.DiscountCents) - sale.SaleDiscountCents;
            // Operação de outra loja (ex.: terminal reativado em outra empresa) ou cuja loja/produto sumiu: só pode ser descartada.
            var otherStore = StoreOf(i.PayloadJson) is { } sid && sid != S.Settings.StoreId;
            var orphan = otherStore || i.LastError is "store_not_found" or "product_not_found" or "cash_session_not_found";
            var reason = otherStore && i.LastError != "store_not_found" ? "Feita em outra loja/instalação anterior deste terminal" : Reason(i.LastError);
            Items.Add(new PendingRow(i.IdempotencyKey, i.Type == SyncTypes.SaleCreate ? "Venda" : "Abertura/fechamento", i.LocalTime.LocalDateTime.ToString("dd/MM/yyyy HH:mm"),
                total > 0 ? AppServices.Money(total) : "—", total, orphan ? reason + " — só é possível descartar" : reason, i.LastError ?? "", i.PayloadJson,
                CanResend: i.Type == SyncTypes.SaleCreate && !orphan));
        }
        Raise(nameof(IsEmpty));
        return Task.CompletedTask;
    }

    private static Guid? StoreOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Name.Equals("storeId", StringComparison.OrdinalIgnoreCase) && prop.Value.TryGetGuid(out var g)) return g;
        }
        catch (JsonException) { }
        return null;
    }

    private void Resend(PendingRow row)
    {
        if (!row.CanResend) { Error = "Esta operação não pode ser reenviada: só é possível descartar."; return; }
        if (row.Type != "Venda") { Error = "Eventos de abertura/fechamento recusados só podem ser descartados (registre o horário correto em Horários)."; return; }
        var sale = JsonSerializer.Deserialize<CreateSaleRequest>(row.PayloadJson, ApiClient.Json)!;
        long newSubtotal = 0;
        foreach (var it in sale.Items)
        {
            if (S.Db.GetProduct(it.ProductId) is not { } p) { Error = "Produto não existe mais no catálogo: só é possível descartar."; return; }
            newSubtotal += (long)Math.Round(p.PriceCents * it.Quantity);
        }
        var charged = row.TotalCents;
        var target = Math.Min(newSubtotal, charged);
        Shell.ShowPrompt(new PromptViewModel
        {
            Title = "Registrar com o preço atual",
            Message = $"Cobrado do cliente: {AppServices.Money(charged)}. Pelo preço atual: {AppServices.Money(newSubtotal)}. "
                      + (newSubtotal > charged ? $"A diferença de {AppServices.Money(newSubtotal - charged)} entra como desconto autorizado por você. " : "")
                      + (newSubtotal < charged ? $"O cliente pagou {AppServices.Money(charged - newSubtotal)} a mais: confira e devolva se for o caso. " : "")
                      + "A venda entra no SEU caixa aberto.",
            ConfirmText = "Registrar venda",
        }, async p =>
        {
            try
            {
                var cur = await S.Api.GetAsync("/api/v1/cash/sessions/current");
                if (cur.ValueKind != JsonValueKind.Object) { p.Error = "Abra o seu caixa antes de registrar."; return false; }
                var payments = new List<SalePaymentRequest>();
                var left = target;
                foreach (var pay in sale.Payments.OrderBy(x => x.Method == 1 ? 1 : 0)) // eletrônicos primeiro, dinheiro por último
                {
                    var amount = pay.Method == 1 ? pay.AmountCents : Math.Min(pay.AmountCents, left);
                    if (amount > 0) payments.Add(pay with { AmountCents = amount });
                    left -= Math.Min(left, amount);
                }
                var resend = sale with
                {
                    IdempotencyKey = row.Key + "-r", CashSessionId = cur.GetProperty("session").GetProperty("id").GetGuid(),
                    Items = sale.Items.Select(i => i with { ClientUnitPriceCents = null, DiscountCents = 0 }).ToList(),
                    SaleDiscountCents = newSubtotal - target, Payments = payments, CreatedOffline = true, RedeemPoints = 0,
                };
                await S.Api.PostAsync("/api/v1/sales", resend);
                S.Db.MarkResult(row.Key, OutboxStatus.Resolved, "registrada novamente com preço atual");
                await LoadAsync();
                return true;
            }
            catch (ApiException ex) { p.Error = ex.Code == "discount_not_authorized" ? "Seu usuário não pode autorizar este desconto." : "Falha: " + ex.Code; }
            catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }

    private void Discard(PendingRow row) => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Descartar operação", Message = $"{row.Type} de {row.When} ({row.Total}). O descarte fica registrado na auditoria com o seu usuário.",
        TextLabel = "Motivo (mínimo 10 caracteres)", ConfirmText = "Descartar",
    }, async p =>
    {
        if (p.Text.Trim().Length < 10) { p.Error = "Explique o motivo (mínimo 10 caracteres)."; return false; }
        try
        {
            await S.Api.PostAsync("/api/v1/sync/discard", new { idempotencyKey = row.Key, reason = p.Text.Trim(), totalCents = row.TotalCents, error = row.ErrorCode });
            S.Db.MarkResult(row.Key, OutboxStatus.Discarded, p.Text.Trim());
            await LoadAsync();
            return true;
        }
        catch (ApiException ex) { p.Error = "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    });
}

// ============================== Avisos ==============================

public sealed record AlertRow(Guid Id, string Title, string Body, string When, bool Unread, string Glyph, Guid? InvoiceId = null, bool CanPay = false);

public sealed class AlertsViewModel(ShellViewModel shell) : OwnerPage(shell, "Avisos", "Quebra de caixa, estoque baixo e outros alertas para o dono")
{
    public ObservableCollection<AlertRow> Items { get; } = [];
    public bool IsEmpty => Items.Count == 0;
    public RelayCommand ReadCommand => new(async p =>
    {
        if (p is AlertRow { Unread: true } r) { await S.Api.PostAsync($"/api/v1/notifications/{r.Id}/read", new { }); await LoadAsync(); }
    });

    // ---------- "Pagar agora" direto no aviso da fatura (mesma lógica da tela Faturas) ----------
    private InvoicesViewModel? _pay;
    private InvoiceRow? _payRow;
    private Guid? _payId;
    /// <summary>Faturas (PIX, cartão/boleto, "Já paguei"): reaproveitada para pagar sem sair dos Avisos.</summary>
    public InvoicesViewModel? Pay { get => _pay; private set => Set(ref _pay, value); }
    public InvoiceRow? PayRow { get => _payRow; private set { if (Set(ref _payRow, value)) Raise(nameof(Paying)); } }
    public bool Paying => PayRow is not null;
    public RelayCommand ClosePayCommand => RelayCommand.Sync(_ => { PayRow = null; _payId = null; });

    /// <summary>Usado só pelas capturas de tela.</summary>
    public void SetPayForPreview(InvoicesViewModel pay, InvoiceRow row) { Pay = pay; _payId = row.Id; PayRow = row; }

    public RelayCommand PayCommand => new(async p =>
    {
        if (p is not AlertRow { CanPay: true } r) return;
        Error = "";
        if (Pay is null)
        {
            Pay = new InvoicesViewModel(Shell);
            // Depois de gerar o PIX ou de "Já paguei" a lista é recarregada: o painel acompanha a fatura atualizada.
            Pay.Items.CollectionChanged += (_, _) => { if (_payId is { } id) PayRow = Pay.Items.FirstOrDefault(x => x.Id == id) ?? PayRow; };
        }
        await Pay.LoadAsync();
        var row = (r.InvoiceId is { } iid ? Pay.Items.FirstOrDefault(x => x.Id == iid) : null) ?? Pay.Items.FirstOrDefault(x => x.IsOpen);
        if (row is null) { Error = Pay.HasError ? Pay.Error : "Fatura não encontrada."; return; }
        _payId = row.Id;
        PayRow = row;
        if (row.IsOpen && !row.HasPix) Pay.PixCommand.Execute(row); // já mostra o QR Code do PIX
    });

    protected override async Task LoadCoreAsync()
    {
        Items.Clear();
        foreach (var n in (await S.Api.GetAsync("/api/v1/notifications")).EnumerateArray())
        {
            var type = n.GetProperty("type").GetString() ?? "";
            Guid? invoice = n.TryGetProperty("invoiceId", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetGuid() : null;
            // Só para fatura ainda a pagar (aviso de pagamento confirmado não mostra o botão) e para quem pode pagar.
            var canPay = type is "invoice.available" or "invoice.due_soon" or "invoice.changed" && S.Session.Can("billing.manage");
            Items.Add(new AlertRow(n.GetProperty("id").GetGuid(), n.GetProperty("title").GetString()!, n.GetProperty("body").GetString()!,
                n.GetProperty("createdAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM/yyyy HH:mm"), n.GetProperty("readAt").ValueKind == JsonValueKind.Null,
                type is "cash.shortage" or "cash.withdrawal" ? Icons.Money : type.StartsWith("invoice.") ? Icons.Money : Icons.Box, invoice, canPay));
        }
        Raise(nameof(IsEmpty));
    }
}
