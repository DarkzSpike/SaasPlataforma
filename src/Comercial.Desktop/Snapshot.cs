using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Comercial.Shared.Branding;

namespace Comercial.Desktop;

/// <summary>
/// Gera capturas das telas com dados de EXEMPLO (sem servidor), para revisão visual e documentação.
/// Uso: Comercial.Desktop.exe --snapshot pasta
/// </summary>
public static class Snapshot
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var cfg = BrandingRules.Preset("moderno");
        cfg.SystemName = "Demo PDV"; cfg.CompanyName = "Loja Demo"; cfg.WelcomeMessage = "Bem-vindo!";
        cfg.PrimaryColor = "#12A86B"; cfg.AccentColor = "#1E8CFF"; cfg.SecondaryColor = "#0F2A5C";
        cfg.BackgroundColor = "#EEF3F1"; cfg.DarkBackgroundColor = "#0B1426"; cfg.DarkSurfaceColor = "#111D33"; cfg.DarkTextColor = "#E8ECF2";
        AppServices.ApplyBranding(cfg);

        var dbPath = Path.Combine(Path.GetTempPath(), $"snapshot-{Guid.NewGuid():N}.db");
        var services = new AppServices(dbPath)
        {
            Online = true,
            Session = new SessionInfo
            {
                TenantName = "Loja Demo", License = "active",
                Permissions = ["sales.read", "sales.cancel", "reports.read", "reports.financial", "stock.read", "cash.review", "cash.operate", "sales.create", "products.write", "stock.entry", "stock.adjust",
                    "purchases.manage", "suppliers.manage", "purchases.approve", "purchases.receive", "customers.read", "customers.manage", "customers.privacy",
                    "loyalty.manage", "finance.read", "finance.manage", "sync.reconcile", "reports.export", "sales.return", "billing.manage", "support.approve", "users.manage", "sales.discount.authorize", "fiscal.read", "fiscal.emit", "fiscal.export", "fiscal.close", "fiscal.send", "payments.configure"],
                Modules = ["pdv", "estoque", "caixa", "app_proprietario", "lucratividade", "white_label", "compras", "fornecedores", "crm", "fidelidade",
                    "contas", "financeiro", "despesas", "trocas", "relatorios_premium", "fiscal"],
            },
        };
        var main = new MainViewModel(services);
        var window = new MainWindow(main) { WindowState = WindowState.Normal, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, Width = 1600, Height = 980, ShowActivated = false, ShowInTaskbar = false };
        window.Show();

        var login = (LoginViewModel)main.Current;
        login.TenantCode = "loja-demo";
        login.Email = "dona@loja-demo.test";
        Save(window, Path.Combine(dir, "01-login.png"));
        login.Status = "Usuário ou senha inválidos.";
        Save(window, Path.Combine(dir, "02-login-erro.png"));
        login.NeedTotp = true; login.Status = "Digite o código de 6 dígitos do aplicativo autenticador.";
        Save(window, Path.Combine(dir, "02b-login-2fa.png"));
        login.NeedTotp = false;
        login.SignupMode = true; login.CompanyName = "Mercadinho São João"; login.OwnerName = "João"; login.Email = "joao@exemplo.test";
        login.Status = "Crie a conta da sua loja. Você recebe um código no e-mail para confirmar.";
        Save(window, Path.Combine(dir, "02c-cadastro.png"));
        login.SignupSent = true; login.Status = "Enviamos um código de 6 dígitos para joao@exemplo.test (vale 30 minutos).";
        Save(window, Path.Combine(dir, "02d-cadastro-codigo.png"));

        var shell = new ShellViewModel(main, "dona@loja-demo.test", startTimers: false);
        main.Current = shell;
        shell.Pos.SetCashForPreview(false);
        Save(window, Path.Combine(dir, "03-pdv-vazio.png"));

        shell.Pos.SetCashForPreview(true);
        shell.Pos.AddLine(Guid.NewGuid(), "Caneca de cerâmica 300 ml", 2, 2490);
        shell.Pos.AddLine(Guid.NewGuid(), "Pilha alcalina AA (par)", 3, 1190);
        shell.Pos.AddLine(Guid.NewGuid(), "Caixa organizadora 20 L", 1, 4590);
        shell.Pos.Paid = "150,00";
        Save(window, Path.Combine(dir, "04-pdv-venda.png"));

        // Pagamento em partes: R$ 50 no dinheiro, o resto no cartão (com "Voltar" se o cartão for recusado).
        shell.Pos.Method = shell.Pos.PaymentOptions[0];
        shell.Pos.Paid = "50,00";
        shell.Pos.FinishCommand.Execute(null);
        Save(window, Path.Combine(dir, "04c-pdv-falta-pagar.png"));
        shell.Pos.Method = shell.Pos.PaymentOptions[2];
        shell.Pos.FinishCommand.Execute(null);
        Save(window, Path.Combine(dir, "04d-pdv-confirmar-cartao.png"));
        shell.Prompt?.CancelCommand?.Execute(null);
        shell.Pos.PaymentLines.Clear();
        shell.Pos.Status = "";
        shell.Pos.Paid = "150,00";
        shell.Pos.Method = shell.Pos.PaymentOptions[0];
        // Loja que emite NFC-e só a pedido + item de preço livre ("100=9,90" → Diversos).
        services.Db.Set("fiscal.mode", "a_pedido");
        shell.Pos.RefreshFiscalMode();
        shell.Pos.AddLine(Guid.NewGuid(), "Diversos", 1, 990);
        shell.Pos.IssueNfce = true;
        Save(window, Path.Combine(dir, "04b-pdv-nfce-a-pedido.png"));
        shell.Pos.IssueNfce = false;


        var dash = new DashboardViewModel(shell);
        dash.Kpis.Add(new Kpi("Faturamento hoje", "R$ 1.284,90", "bruto • 37 venda(s)", "\uE7BF", "accent"));
        dash.Kpis.Add(new Kpi("Lucro do dia", "R$ 602,15", "vendas − custo dos produtos", "\uE8C7", "primary"));
        dash.Kpis.Add(new Kpi("Faturamento no mês", "R$ 21.940,30", "bruto • 612 venda(s)", "\uE787", "accent"));
        dash.Kpis.Add(new Kpi("Lucro do mês", "R$ 10.112,80", "vendas − custo dos produtos", "\uE8C7", "primary"));
        dash.Kpis.Add(new Kpi("Caixas abertos", "1", "agora", Icons.Bank, "neutral"));
        dash.Kpis.Add(new Kpi("Quebra de caixa", "R$ 37,50", "hoje R$ 12,50 · 2 fechamento(s) no mês", Icons.Money, "danger"));
        dash.Kpis.Add(new Kpi("Estoque baixo", "2", "produto(s) para repor", "\uE7B8", "warn"));
        var rnd = new Random(7);
        foreach (var b in OwnerPage.BuildBars(Enumerable.Range(0, 14).Select(i =>
                 { var rev = rnd.Next(60000, 160000); return (DateTime.Today.AddDays(i - 13).ToString("dd/MM"), (long)rev, (long)(rev * (0.38 + rnd.NextDouble() * 0.12))); })))
            dash.Bars.Add(b);
        foreach (var (when, op, pay, total) in new[] { ("18/10 14:32", "Dona Demo", "PIX", "R$ 74,70"), ("18/10 14:10", "Carlos (caixa)", "Dinheiro", "R$ 24,90"), ("18/10 13:55", "Carlos (caixa)", "Crédito", "R$ 139,60"), ("18/10 13:21", "Dona Demo", "Débito", "R$ 45,90") })
            dash.Recent.Add(new SaleRow(Guid.NewGuid(), when, op, "", pay, total, "Concluída", false, false));
        dash.Note = "Valores consideram apenas dados já sincronizados com o servidor.";
        dash.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(dash, ["HasBars"]);
        shell.ShowForPreview("dashboard", dash);
        Save(window, Path.Combine(dir, "05-painel.png"));

        var hist = new HistoryViewModel(shell);
        var rows = new[]
        {
            new SaleRow(Guid.NewGuid(), "18/10 14:32", "Dona Demo", "5 un. (3 prod.)", "PIX", "R$ 74,70", "Concluída", false, false),
            new SaleRow(Guid.NewGuid(), "18/10 14:10", "Carlos (caixa)", "1 un. (1 prod.)", "Dinheiro", "R$ 24,90", "Concluída", false, true),
            new SaleRow(Guid.NewGuid(), "18/10 13:55", "Carlos (caixa)", "4 un. (2 prod.)", "Crédito", "R$ 139,60", "Cancelada", true, false),
            new SaleRow(Guid.NewGuid(), "18/10 13:21", "Dona Demo", "2 un. (2 prod.)", "Débito", "R$ 45,90", "Concluída", false, false),
            new SaleRow(Guid.NewGuid(), "18/10 12:47", "Dona Demo", "6 un. (1 prod.)", "Dinheiro", "R$ 41,40", "Concluída", false, false),
        };
        foreach (var r in rows) hist.Sales.Add(r);
        hist.Summary = "4 venda(s) concluída(s) • R$ 186,90 • 1 cancelada(s)";
        hist.Selected = null;
        hist.ApplyDetail(rows[0], JsonSerializer.Deserialize<JsonElement>("""
            {"discountCents":0,"changeCents":0,"cancelReason":null,"items":[
             {"productName":"Caneca de cerâmica 300 ml","quantity":2,"unitPriceCents":2490,"totalCents":4980},
             {"productName":"Copo de vidro 350 ml","quantity":2,"unitPriceCents":690,"totalCents":1380},
             {"productName":"Pilha alcalina AA (par)","quantity":1,"unitPriceCents":1190,"totalCents":1190}]}
            """));
        typeof(HistoryViewModel).GetField("_selected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(hist, rows[0]);
        hist.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(hist, ["HasSelection"]);
        hist.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(hist, ["Selected"]);
        shell.ShowForPreview("history", hist);
        Save(window, Path.Combine(dir, "06-historico.png"));

        var profit = new ProfitViewModel(shell);
        profit.Apply(JsonSerializer.Deserialize<JsonElement>("""
            {"totals":{"salesCount":187,"revenueCents":684230,"costCents":341580,"grossProfitCents":342650,"marginPercent":50.1,"saleLevelDiscountCents":1250},
             "products":[
              {"name":"Caixa organizadora 20 L","quantity":38,"revenueCents":174420,"costCents":87400,"grossProfitCents":87020,"marginPercent":49.9},
              {"name":"Caneca de cerâmica 300 ml","quantity":61,"revenueCents":151890,"costCents":67100,"grossProfitCents":84790,"marginPercent":55.8},
              {"name":"Caderno universitário 10 matérias","quantity":44,"revenueCents":131560,"costCents":72600,"grossProfitCents":58960,"marginPercent":44.8},
              {"name":"Copo de vidro 350 ml","quantity":152,"revenueCents":104880,"costCents":42560,"grossProfitCents":62320,"marginPercent":59.4},
              {"name":"Pilha alcalina AA (par)","quantity":71,"revenueCents":84490,"costCents":42600,"grossProfitCents":41890,"marginPercent":49.6},
              {"name":"Toalha de rosto algodão","quantity":19,"revenueCents":37810,"costCents":29320,"grossProfitCents":8490,"marginPercent":22.5}],
             "daily":[
              {"date":"2026-10-12","revenueCents":81200,"grossProfitCents":39800},{"date":"2026-10-13","revenueCents":102300,"grossProfitCents":52100},
              {"date":"2026-10-14","revenueCents":95400,"grossProfitCents":47300},{"date":"2026-10-15","revenueCents":121800,"grossProfitCents":61900},
              {"date":"2026-10-16","revenueCents":88700,"grossProfitCents":43100},{"date":"2026-10-17","revenueCents":134500,"grossProfitCents":68700},
              {"date":"2026-10-18","revenueCents":60330,"grossProfitCents":29650}],
             "note":"Lucro bruto estimado = vendas − custo histórico registrado em cada venda. Não inclui despesas, impostos nem taxas de cartão; não é resultado contábil."}
            """));
        shell.ShowForPreview("profit", profit);
        Save(window, Path.Combine(dir, "07-lucro.png"));

        var stock = new StockViewModel(shell);
        var thumb = FormViewModel.Load(SamplePng(0x12, 0xA8, 0x6B));
        StockRow R(string n, string sku, decimal q, long price, bool img = false) =>
            new(Guid.NewGuid(), n, sku, null, q, 5, price, price / 2, img) { Thumb = img ? thumb : null };
        var rowsStock = new[]
        {
            R("Toalha de rosto algodão", "TOA004", 3, 1990), R("Caixa organizadora 20 L", "ORG006", 4, 4590, true),
            R("Caneca de cerâmica 300 ml", "CAN001", 38, 2490, true), R("Caderno universitário 10 matérias", "CAD002", 25, 2990),
            R("Copo de vidro 350 ml", "COP005", 118, 690), R("Pilha alcalina AA (par)", "PIL003", 57, 1190),
        };
        // Como na tela real: a página abre vazia e os produtos chegam depois (resposta do servidor).
        shell.ShowForPreview("stock", stock);
        Save(window, Path.Combine(dir, "08-estoque-carregando.png"));
        stock.SetRows(rowsStock);
        Save(window, Path.Combine(dir, "08-estoque.png"));

        stock.InventoryMode = true;
        rowsStock[0].Counted = "2"; rowsStock[2].Counted = "38"; rowsStock[4].Counted = "120";
        Save(window, Path.Combine(dir, "08b-inventario.png"));
        stock.InventoryMode = false;

        var form = new FormViewModel { Title = "Novo produto", Message = "Cadastre o produto e, se quiser, a quantidade que já está na loja.", ConfirmText = "Cadastrar produto", AllowImage = true };
        form.Fields.Add(new FormField("name", "Nome do produto", "Garrafa térmica 500 ml", Icons.Box));
        form.Fields.Add(new FormField("sku", "Código interno (SKU)", "GAR007", Icons.Edit));
        form.Fields.Add(new FormField("barcode", "Código de barras", "2000001234565", Icons.Chart) { ActionText = "Gerar código", ActionCommand = RelayCommand.Sync(_ => { }), Note = "Código gerado pelo sistema (único). Imprima a etiqueta no botão Etiquetas do Estoque." });
        form.Fields.Add(new FormField("cost", "Quanto você pagou pelo produto (custo R$)", "20,00", Icons.Money));
        form.Fields.Add(new FormField("markup", "% de lucro em cima do custo", "100", Icons.Chart));
        form.Fields.Add(new FormField("price", "Preço de venda (R$)", "40,00", Icons.Money) { IsReadOnly = true, Note = "Calculado automaticamente • lucro de R$ 20,00 por unidade" });
        form.Fields.Add(new FormField("min", "Estoque mínimo", "5", Icons.Box));
        form.Fields.Add(new FormField("qty", "Quantidade inicial na loja", "24", Icons.Add));
        form.Preview = thumb;
        shell.ShowForm(form, _ => Task.FromResult(true));
        Save(window, Path.Combine(dir, "08c-novo-produto.png"));
        shell.Form = null;

        var cash = new CashHistoryViewModel(shell);
        cash.Sessions.Add(new CashRow("18/10 08:02", "—", "Aberto", "R$ 150,00", "—", "—", "—", false, false, ""));
        cash.Sessions.Add(new CashRow("17/10 08:05", "17/10 18:31", "Fechado", "R$ 150,00", "R$ 1.032,40", "R$ 1.030,40", "-R$ 2,00", true, false, "faltou moeda de troco"));
        cash.Sessions.Add(new CashRow("16/10 08:01", "16/10 18:22", "Fechado", "R$ 150,00", "R$ 884,10", "R$ 884,10", "R$ 0,00", false, false, ""));
        shell.ShowForPreview("cash", cash);
        Save(window, Path.Combine(dir, "09-caixas.png"));

        shell.ShowForPreview("pos", shell.Pos);
        shell.ShowPrompt(new PromptViewModel
        {
            Title = "Fechar caixa", Message = "Conte o dinheiro da gaveta e informe o valor. Diferenças exigem justificativa.",
            ValueLabel = "Dinheiro contado (R$)", TextLabel = "Justificativa (se houver diferença)", ConfirmText = "Fechar caixa",
        }, _ => Task.FromResult(true));
        Save(window, Path.Combine(dir, "10-fechar-caixa.png"));
        shell.Prompt = null;

        shell.ShowSaleDone(12740, 15000, "Dinheiro", new Receipt(DateTime.Now, [new ReceiptLine("Caneca de cerâmica 300 ml", 2, 2490, 4980)], 12740, 0, 15000, "Dinheiro", "Maria Silva", 127));
        Save(window, Path.Combine(dir, "11-troco.png"));
        shell.SaleDone = null;

        shell.Pos.SetCustomerForPreview("Maria Silva", 340);
        shell.Pos.Redeem = "200";
        Save(window, Path.Combine(dir, "12-pdv-cliente-pontos.png"));

        var pur = new PurchasesViewModel(shell);
        pur.Suppliers.Add(new SupplierRow(Guid.NewGuid(), "Distribuidora Alfa", "12.345.678/0001-99", "Carlos", "(11) 3333-4444", 3));
        pur.Suppliers.Add(new SupplierRow(Guid.NewGuid(), "Utilidades Beta", "98.765.432/0001-10", "Ana", "(11) 2222-1111", 7));
        pur.Orders.Add(new OrderRow(Guid.NewGuid(), 3, "Distribuidora Alfa", "Aprovado", "Approved", "18/10/2026", 3, "R$ 1.284,00"));
        pur.Orders.Add(new OrderRow(Guid.NewGuid(), 2, "Utilidades Beta", "Recebido em parte", "PartiallyReceived", "15/10/2026", 2, "R$ 640,00"));
        pur.Orders.Add(new OrderRow(Guid.NewGuid(), 1, "Distribuidora Alfa", "Recebido", "Received", "10/10/2026", 4, "R$ 2.110,50"));
        typeof(PurchasesViewModel).GetField("_selected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(pur, pur.Orders[0]);
        pur.Lines.Add(new OrderLine(Guid.NewGuid(), "Toalha de rosto algodão", 24, 0, 900) { ReceiveQty = "24" });
        pur.Lines.Add(new OrderLine(Guid.NewGuid(), "Caixa organizadora 20 L", 12, 0, 2300) { ReceiveQty = "12" });
        pur.Lines.Add(new OrderLine(Guid.NewGuid(), "Pilha alcalina AA (par)", 60, 0, 600) { ReceiveQty = "60" });
        pur.DetailTitle = "Pedido #3 — Distribuidora Alfa"; pur.DetailInfo = "Aprovado • criado em 18/10/2026 • total R$ 1.284,00";
        foreach (var n in new[] { "Viewing", "CanReceive", "Selected" })
            pur.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(pur, [n]);
        shell.ShowForPreview("purchases", pur);
        Save(window, Path.Combine(dir, "13-compras.png"));

        var cus = new CustomersViewModel(shell);
        cus.Customers.Add(new CustomerRow(Guid.NewGuid(), "Maria Silva", "12345678901", "(11) 98888-7777", "maria@exemplo.com", true));
        cus.Customers.Add(new CustomerRow(Guid.NewGuid(), "João Pereira", "98765432100", "(11) 97777-6666", "", false));
        typeof(CustomersViewModel).GetField("_selected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(cus, cus.Customers[0]);
        cus.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(cus, ["HasSelection"]);
        cus.DetailTitle = "Maria Silva"; cus.DetailInfo = "12 compra(s) • total R$ 1.932,40 • aceita receber ofertas"; cus.PointsText = "340 ponto(s) disponíveis";
        cus.Recent.Add(new SaleRow(Guid.NewGuid(), "18/10/2026 14:32", "", "", "", "R$ 74,70", "Concluída", false, false));
        cus.Recent.Add(new SaleRow(Guid.NewGuid(), "11/10/2026 10:05", "", "", "", "R$ 139,60", "Concluída", false, false));
        cus.Statement.Add(new StatementRow("18/10 14:32", "Compra", "+74", "compra", true));
        cus.Statement.Add(new StatementRow("15/10 09:10", "Resgate", "-100", "resgate na venda", false));
        cus.Statement.Add(new StatementRow("11/10 10:05", "Compra", "+139", "compra", true));
        shell.ShowForPreview("customers", cus);
        Save(window, Path.Combine(dir, "14-clientes.png"));

        // Cobrança do fiado pelo WhatsApp da loja: dia escolhido e botão verde.
        cus.CreditText = "Fiado: deve R$ 49,80 • limite R$ 300,00 • disponível R$ 250,20";
        cus.SetBillingForPreview(10, "10/10/2026");
        Save(window, Path.Combine(dir, "14b-clientes-whatsapp.png"));
        shell.ShowPrompt(new PromptViewModel
        {
            Title = "Cobrar pelo WhatsApp: Maria Silva",
            Message = "Olá, Maria! Aqui é da Loja Demo. Seu saldo no crediário é de R$ 49,80. Compras em aberto: 28/10: Caneca de cerâmica 300 ml x2 (R$ 49,80). Para pagar ou tirar dúvidas, responda esta mensagem.\n\nEnviar agora para o celular •••7777?",
            ConfirmText = "Enviar cobrança",
        }, _ => Task.FromResult(true));
        Save(window, Path.Combine(dir, "14c-cobranca-whatsapp.png"));
        shell.Prompt = null;

        var settingsPage = new SettingsViewModel(shell);
        settingsPage.SetForPreview();
        shell.Page = settingsPage;
        Save(window, Path.Combine(dir, "32-configuracoes.png"));
        var campaignsPage = new CampaignsViewModel(shell);
        campaignsPage.SetForPreview();
        shell.Page = campaignsPage;
        Save(window, Path.Combine(dir, "33-campanhas.png"));

        var fin = new FinanceViewModel(shell);
        fin.Kpis.Add(new Kpi("A pagar no mês", "R$ 4.320,00", "em aberto", Icons.Calendar, "warn"));
        fin.Kpis.Add(new Kpi("A receber no mês", "R$ 1.150,00", "em aberto", Icons.Money, "accent"));
        fin.Kpis.Add(new Kpi("Vencidos", "R$ 380,00", "1 título(s)", Icons.History, "warn"));
        fin.Kpis.Add(new Kpi("Pago no mês", "R$ 6.910,40", "recebido R$ 2.300,00", Icons.Chart, "primary"));
        fin.Titles.Add(new TitleRow(Guid.NewGuid(), "Pagar", "Energia elétrica", "Energia", "Concessionária", "12/10/2026", "R$ 380,00", "Em aberto", true, true, false));
        fin.Titles.Add(new TitleRow(Guid.NewGuid(), "Pagar", "Compra #3 — NF 4512", "Compra de mercadoria", "Distribuidora Alfa", "17/11/2026", "R$ 1.284,00", "Em aberto", false, true, false));
        fin.Titles.Add(new TitleRow(Guid.NewGuid(), "Pagar", "Aluguel (2/12)", "Aluguel", "Imobiliária", "05/11/2026", "R$ 2.500,00", "Em aberto", false, true, false));
        fin.Titles.Add(new TitleRow(Guid.NewGuid(), "Receber", "Venda a prazo — Mercado Gama", "Vendas a prazo", "Mercado Gama", "30/10/2026", "R$ 1.150,00", "Em aberto", false, true, false));
        fin.Titles.Add(new TitleRow(Guid.NewGuid(), "Pagar", "Internet", "Internet/telefone", "Provedor", "08/10/2026", "R$ 129,90", "Pago", false, false, true));
        shell.ShowForPreview("finance", fin);
        Save(window, Path.Combine(dir, "15-financeiro.png"));

        var pend = new PendingViewModel(shell);
        pend.Items.Add(new PendingRow("sale-1", "Venda", "17/10/2026 19:42", "R$ 49,80", 4980, PendingViewModel.Reason("price_mismatch"), "price_mismatch", "{}", true));
        pend.Items.Add(new PendingRow("sale-2", "Venda", "17/10/2026 18:10", "R$ 24,90", 2490, PendingViewModel.Reason("cash_not_open"), "cash_not_open", "{}", true));
        pend.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(pend, ["IsEmpty"]);
        shell.ShowForPreview("pending", pend);
        Save(window, Path.Combine(dir, "16-pendencias.png"));

        var alerts = new AlertsViewModel(shell);
        alerts.Items.Add(new AlertRow(Guid.NewGuid(), "Quebra de caixa: R$ 12,50", "Caixa de Carlos (Matriz) fechado com falta de R$ 12,50. Esperado R$ 1.032,40, contado R$ 1.019,90. Justificativa: troco errado.", "18/10/2026 18:31", true, Icons.Money));
        alerts.Items.Add(new AlertRow(Guid.NewGuid(), "Estoque baixo: Toalha de rosto algodão", "Restam 3 unidade(s) de Toalha de rosto algodão (mínimo 5). Considere repor.", "18/10/2026 14:05", false, Icons.Box));
        alerts.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(alerts, ["IsEmpty"]);
        shell.ShowForPreview("alerts", alerts);
        Save(window, Path.Combine(dir, "17-avisos.png"));

        var inv = new InvoicesViewModel(shell);
        static System.Text.Json.JsonElement J(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement;
        var due = DateTimeOffset.Now.AddDays(2);
        inv.Items.Add(InvoicesViewModel.Row(J($$"""{"id":"{{Guid.NewGuid()}}","amountCents":14990,"dueAt":"{{due:O}}","periodStart":"{{due:O}}","periodEnd":"{{due.AddMonths(1):O}}","status":"aberta","overdue":false,"daysToDue":2,"pixCopyPaste":"00020126580014br.gov.bcb.pix0136a1b2c3d4-exemplo-de-codigo-pix-copia-e-cola5204000053039865406149.905802BR5913LOJA DEMO6009SAO PAULO62070503***6304ABCD","pixQrBase64":"{{Convert.ToBase64String(SamplePng(20, 20, 20))}}","pixExpiresAt":"{{DateTimeOffset.Now.AddHours(23):O}}"}""")));
        inv.Items.Add(InvoicesViewModel.Row(J($$"""{"id":"{{Guid.NewGuid()}}","amountCents":14990,"dueAt":"{{due.AddMonths(-1):O}}","periodStart":"{{due.AddMonths(-1):O}}","periodEnd":"{{due:O}}","status":"paga","overdue":false,"daysToDue":-28,"paidAt":"{{due.AddMonths(-1).AddDays(-1):O}}","paidMethod":"pix"}""")));
        inv.Banner = "Sua fatura de R$ 149,90 vence em 2 dia(s).";
        inv.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(inv, ["IsEmpty"]);
        shell.ShowForPreview("invoices", inv);
        Save(window, Path.Combine(dir, "18-faturas.png"));

        var alertsPay = new AlertsViewModel(shell);
        alertsPay.Items.Add(new AlertRow(Guid.NewGuid(), "Nova fatura: R$ 149,90", $"Sua fatura do sistema vence em {due:dd/MM/yyyy}. Pague por PIX, boleto ou cartão em Faturas.", DateTime.Now.ToString("dd/MM/yyyy HH:mm"), true, Icons.Money, inv.Items[0].Id, true));
        alertsPay.Items.Add(new AlertRow(Guid.NewGuid(), "Estoque baixo: Toalha de rosto algodão", "Restam 3 unidade(s) de Toalha de rosto algodão (mínimo 5). Considere repor.", "18/10/2026 14:05", false, Icons.Box));
        alertsPay.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(alertsPay, ["IsEmpty"]);
        alertsPay.SetPayForPreview(inv, inv.Items[0]);
        shell.ShowForPreview("alerts", alertsPay);
        Save(window, Path.Combine(dir, "18b-avisos-pagar.png"));

        var sup = new SupportViewModel(shell);
        sup.Options.Add(new SupportOption { Key = "products.write", Name = "cadastro de produtos", Checked = true });
        sup.Options.Add(new SupportOption { Key = "stock.adjust", Name = "ajuste de estoque", Checked = true });
        sup.Options.Add(new SupportOption { Key = "users.manage", Name = "usuários" });
        sup.Options.Add(new SupportOption { Key = "devices.manage", Name = "terminais" });
        sup.Requests.Add(new SupportRequestRow(Guid.NewGuid(), SupportViewModel.Ops("users.manage") + " por 15 min — Cliente pediu para desbloquear o usuário do caixa 2", "18/10 15:02"));
        sup.Sessions.Add(new SupportSessionRow(Guid.NewGuid(), "18/10/2026 14:55 — Ajustar preços que vieram errados da planilha", "ATIVA, pode alterar: " + SupportViewModel.Ops("products.write,stock.adjust") + " • até 15:55", true));
        sup.Sessions.Add(new SupportSessionRow(Guid.NewGuid(), "12/10/2026 10:20 — Dúvida sobre fechamento de caixa", "Encerrada: encerrada pelo suporte", false));
        sup.GetType().GetMethod("Raise", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(sup, ["NoRequests"]);
        shell.ShowForPreview("support", sup);
        Save(window, Path.Combine(dir, "19-suporte.png"));

        var emp = new EmployeesViewModel(shell);
        foreach (var (k, n) in new[] { ("operator", "Operador de caixa"), ("stockist", "Estoquista"), ("finance", "Financeiro"), ("manager", "Gerente") }) emp.RoleOptions.Add(new RoleOption(k, n));
        foreach (var (k, n, on) in new[] { ("sales.create", "Vender (PDV)", true), ("sales.read", "Ver histórico de vendas", true), ("cash.operate", "Abrir/fechar o próprio caixa e sangria", true),
                     ("operating.openclose", "Abrir e fechar a loja", true), ("customers.manage", "Cadastrar clientes", true), ("sales.cancel", "Cancelar vendas", false),
                     ("sales.discount.authorize", "Autorizar desconto no total da venda", false), ("stock.entry", "Lançar entrada de mercadoria", true), ("reports.read", "Ver painel e relatórios", false), ("products.write", "Cadastrar e editar produtos", false) })
            emp.PermissionsList.Add(new PermissionOption { Key = k, Name = n, CanGrant = true, FromRole = on && k != "stock.entry", Checked = on });
        emp.Users.Add(new EmployeeRow { Id = Guid.NewGuid(), Name = "Dona Demo", Email = "dona@loja-demo.test", Role = "owner", Active = true, IsMe = true });
        emp.Users.Add(new EmployeeRow { Id = Guid.NewGuid(), Name = "Carlos Souza", Email = "carlos@loja-demo.test", Role = "operator", Active = true });
        emp.Users.Add(new EmployeeRow { Id = Guid.NewGuid(), Name = "Ana Lima", Email = "ana@loja-demo.test", Role = "manager", Active = true });
        emp.Users.Add(new EmployeeRow { Id = Guid.NewGuid(), Name = "Pedro Alves", Email = "pedro@loja-demo.test", Role = "stockist", Active = false });
        var keepPerms = emp.PermissionsList.Select(x => (x.Key, x.Name, x.FromRole, x.Checked)).ToList();
        emp.Selected = emp.Users[1];
        emp.PermissionsList.Clear();
        foreach (var (k, n, fr, c) in keepPerms) emp.PermissionsList.Add(new PermissionOption { Key = k, Name = n, CanGrant = true, FromRole = fr, Checked = c });
        shell.ShowForPreview("employees", emp);
        Save(window, Path.Combine(dir, "20-funcionarios.png"));

        var acc = new AccountViewModel(shell);
        using (var gen = new QRCoder.QRCodeGenerator())
        using (var data = gen.CreateQrCode("otpauth://totp/Sistema%20Comercial:dona@loja-demo.test?secret=JBSWY3DPEHPK3PXP&issuer=Demo", QRCoder.QRCodeGenerator.ECCLevel.M))
            acc.Qr = FormViewModel.Load(new QRCoder.PngByteQRCode(data).GetGraphic(8));
        acc.Secret = "JBSW Y3DP EHPK 3PXP"; acc.SettingUp = true;
        acc.Info = "Abra o aplicativo autenticador no celular, leia o QR Code (ou digite a chave) e confirme com o código de 6 dígitos.";
        shell.ShowForPreview("account", acc);
        Save(window, Path.Combine(dir, "21-minha-conta-2fa.png"));

        shell.SetUpdateForPreview("Nova versão 1.1.0 disponível: PIX mais rápido e correções no fechamento de caixa.");
        shell.ShowForPreview("help", new HelpViewModel(shell));
        Save(window, Path.Combine(dir, "22-ajuda-e-atualizacao.png"));

        // Fiscal e Contabilidade (dados de exemplo, sem valor fiscal).
        var fis = new FiscalViewModel(shell);
        fis.ShowForPreview("geral", v =>
        {
            v.Kpis.Add(new Kpi("NFC-e autorizadas", "412", "R$ 38.954,20", "", "primary"));
            v.Kpis.Add(new Kpi("Canceladas", "3", "R$ 187,50", "", "warn"));
            v.Kpis.Add(new Kpi("Rejeitadas/denegadas", "1", "precisam de análise", "", "danger"));
            v.Kpis.Add(new Kpi("Pendentes", "2", "1 sem autorizar • 1 sem XML", "", "warn"));
            v.Kpis.Add(new Kpi("Vendas do período", "R$ 39.141,70", "416 venda(s)", "", "accent"));
            foreach (var x in new[] { "• 1 nota(s) aguardando transmissão", "• 1 nota(s) rejeitada(s) para corrigir", "• 1 nota(s) autorizada(s) sem XML original arquivado", "• 1 caixa(s) ainda aberto(s)" }) v.Pendencies.Add(x);
            foreach (var x in new[] { "Dinheiro: R$ 12.410,00", "PIX: R$ 15.022,70", "Cartão de crédito: R$ 7.890,00", "Cartão de débito: R$ 3.819,00" }) v.PaymentLines.Add(x);
            v.OverviewStatus = "Fechamento de Outubro/2026: em andamento\nÚltimo envio ao contador: 05/10/2026 08:00 (09/2026) — Aceito pelo servidor de e-mail";
        });
        shell.ShowForPreview("fiscal", fis);
        Save(window, Path.Combine(dir, "23-fiscal-geral.png"));
        fis.ShowForPreview("documentos", v =>
        {
            string K(int n) => "3526101122233300018165001" + n.ToString().PadLeft(19, '0');
            v.Docs.Add(new FiscalDocRow(Guid.NewGuid(), "26/10/2026 18:42", "412", "1", "NFC-e 65", "Consumidor não identificado", "R$ 89,90", "Autorizada", "autorizada", 2, "Autorizado o uso da NF-e", "https://x", K(412), "135260004120012", true, false));
            v.Docs.Add(new FiscalDocRow(Guid.NewGuid(), "26/10/2026 18:30", "411", "1", "NFC-e 65", "Maria Souza (cliente da venda)", "R$ 25,00", "Cancelada", "cancelada", 5, "Cancelamento homologado", "https://x", K(411), "135260004110017", true, true));
            v.Docs.Add(new FiscalDocRow(Guid.NewGuid(), "26/10/2026 17:55", "—", "—", "NFC-e 65", "Consumidor não identificado", "R$ 12,50", "Rejeitada", "rejeitada", 3, "Rejeição 778: NCM inexistente", null, "", "", false, false));
            v.Docs.Add(new FiscalDocRow(Guid.NewGuid(), "26/10/2026 17:40", "—", "—", "NFC-e 65", "Consumidor não identificado", "R$ 40,00", "Pendente", "pendente", 1, "SEFAZ processando; nova consulta em instantes.", null, "", "", false, false));
            v.PageInfo = "416 documento(s) • página 1 de 9";
        });
        Save(window, Path.Combine(dir, "24-fiscal-documentos.png"));
        fis.ShowForPreview("fechamento", v =>
        {
            v.Checks.Add(new CheckRow("Todas as vendas têm documento fiscal registrado", "concluido", "Concluído", "Todas as vendas concluídas têm documento registrado.", "", false));
            v.Checks.Add(new CheckRow("Notas autorizadas com XML original guardado", "divergencia", "Com divergência", "1 nota(s) sem XML original no arquivo fiscal. Use \"Buscar XMLs no provedor\".", "   – 12/10/2026 14:03 • venda 8f2a91c0 • NFC-e 233/1 • XML original ainda não arquivado", true));
            v.Checks.Add(new CheckRow("Cancelamentos com o XML do evento", "concluido", "Concluído", "Eventos de cancelamento arquivados.", "", false));
            v.Checks.Add(new CheckRow("Documentos rejeitados ou denegados analisados", "em_analise", "Em análise", "1 documento(s) para analisar.", "", false));
            v.Checks.Add(new CheckRow("Vendas com o documento fiscal esperado", "pendente", "Pendente", "1 nota(s) ainda não autorizada(s).", "", false));
            v.Checks.Add(new CheckRow("Conciliação de pagamentos", "concluido", "Concluído", "PIX do caixa conciliado com o Mercado Pago. Cartões: conferir no extrato da adquirente.", "", false));
            v.Checks.Add(new CheckRow("Arquivos e relatórios prontos para exportação", "pendente", "Pendente", "Nenhum pacote gerado para o mês.", "", false));
            v.ClosingStatus = "Fechamento ainda não concluído. Resolva as pendências (ou registre nas observações como foram tratadas) e conclua.";
        });
        Save(window, Path.Combine(dir, "25-fiscal-fechamento.png"));
        fis.ShowForPreview("contador", v =>
        {
            v.AccountantSummary = "Contabilidade Exemplo — Ana Lima\nE-mail: ana@contabilidade.exemplo (+ fiscal@contabilidade.exemplo)\nEnvio automático: todo dia 5 às 8h, com o pacote do mês anterior.";
            v.Deliveries.Add(new DeliveryRow(Guid.NewGuid(), "05/10/2026 08:00", "09/2026", "automático", "aceito_pelo_servidor", "Aceito pelo servidor de e-mail", "ana@contabilidade.exemplo, fiscal@contabilidade.exemplo", "Aceito pelo servidor de e-mail (a leitura pelo contador não é confirmável).", "automático"));
            v.Deliveries.Add(new DeliveryRow(Guid.NewGuid(), "05/09/2026 08:00", "08/2026", "automático", "bloqueado", "Bloqueado (pendências)", "ana@contabilidade.exemplo", "Notas autorizadas com XML original guardado: 2 nota(s) sem XML original.", "automático"));
        });
        Save(window, Path.Combine(dir, "26-fiscal-contador.png"));
        fis.ShowForPreview("relatorios", v =>
        {
            var table = new System.Data.DataTable();
            foreach (var h in new[] { "Meio", "Transacoes", "Valor bruto vendido", "Cancelado", "Estornado", "Liquido registrado", "Taxas", "Recebido conciliado", "Diferenca conciliacao" }) table.Columns.Add(h);
            table.Rows.Add("Dinheiro", "188", "12.410,00", "45,00", "30,00", "12.380,00", "não disponível", "conferido no fechamento de caixa", "—");
            table.Rows.Add("PIX", "141", "15.022,70", "0,00", "0,00", "15.022,70", "não disponível", "14.972,70", "-50,00");
            table.Rows.Add("Cartão de crédito", "58", "7.890,00", "142,50", "0,00", "7.890,00", "não disponível", "sem integração com adquirente", "—");
            table.Rows.Add("Cartão de débito", "29", "3.819,00", "0,00", "0,00", "3.819,00", "não disponível", "sem integração com adquirente", "—");
            v.ReportView = table.DefaultView;
            foreach (var x in new[] { "Total vendido (vendas concluídas): 39.141,70", "PIX vendido × confirmado pelo Mercado Pago: 15.022,70 × 14.972,70", "Divergências de conciliação PIX: 1" }) v.ReportTotals.Add(x);
            v.ReportNotes = "• Valor vendido NÃO é o mesmo que valor recebido: dinheiro é conferido no fechamento do caixa; PIX do caixa é conciliado com o Mercado Pago;";
        });
        Save(window, Path.Combine(dir, "27-fiscal-relatorio.png"));

        // DANFE NFC-e (exemplo de HOMOLOGAÇÃO, sem valor fiscal) como sai na impressora do Windows.
        var danfe = new Comercial.Desktop.Core.Hardware.NfceDanfe("autorizada", "100 - Autorizado o uso da NF-e", 123, 1, "35261011222333000181650010000001231234567890",
            "135260000123456", DateTimeOffset.Now, DateTimeOffset.Now, true, "https://www.homologacao.nfce.fazenda.sp.gov.br/qrcode?p=35261011222333000181650010000001231234567890|3|2|1|ABC",
            "https://www.homologacao.nfce.fazenda.sp.gov.br/consulta", "LOJA TESTE LTDA", "11222333000181", "111222333444", "RUA DAS FLORES, 100 - CENTRO - SAO PAULO/SP",
            [new("VAR-1", "Caneca de porcelana", 2, "UN", 1990, 3980), new("VAR-7", "Pilha alcalina AA (par)", 1, "UN", 1190, 1190)], 5170, 0, 5170,
            [new("Dinheiro", 6000)], 830);
        var paper = new Window { Width = 360, Height = 900, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            Content = new System.Windows.Controls.FlowDocumentScrollViewer { Document = ReceiptPrinter.BuildDanfe(danfe), Background = Brushes.White, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled } };
        paper.Show();
        Save(paper, Path.Combine(dir, "28-danfe-nfce.png"));
        paper.Close();
        shell.ShowSaleDone(5170, 6000, "Dinheiro", new Receipt(DateTime.Now, [new ReceiptLine("Caneca de porcelana", 2, 1990, 3980)], 5170, 0, 6000, "Dinheiro"));
        shell.SaleDone!.SetDanfe(danfe);
        shell.SaleDone.FiscalText = "NFC-e 123 autorizada (homologação, sem valor fiscal)";
        Save(window, Path.Combine(dir, "29-venda-nfce.png"));
        shell.SaleDone = null;

        // White label: outra empresa com logo, ícone, ilustração, fundo e textos próprios.
        var brand = BrandingRules.Preset("vibrante");
        brand.SystemName = "Bom Preço"; brand.CompanyName = "Mercado Bom Preço"; brand.WelcomeMessage = "Olá, equipe Bom Preço!";
        brand.Tagline = "Qualidade e economia"; brand.LoginSubtitle = "Entre para abrir o caixa da loja.";
        brand.HeaderSubtitle = "O mercado do seu bairro"; brand.Feature1 = "Entrega grátis"; brand.Feature2 = "Preço baixo"; brand.Feature3 = "Desde 1998";
        var assets = new Dictionary<Guid, byte[]>();
        Guid Add(byte[] png) { var id = Guid.NewGuid(); assets[id] = png; return id; }
        brand.LogoDarkAssetId = Add(SamplePng(250, 200, 40, 240, 80));
        brand.IconAssetId = Add(SamplePng(250, 120, 30));
        brand.LoginImageAssetId = Add(SamplePng(60, 160, 240, 420, 280));
        brand.LoginBackgroundAssetId = Add(SamplePng(200, 40, 90, 640, 400));
        AppServices.ApplyBranding(brand, id => assets.GetValueOrDefault(id));
        main.Current = shell;
        Save(window, Path.Combine(dir, "30-marca-pdv.png"));
        main.Current = new LoginViewModel(main);
        Save(window, Path.Combine(dir, "31-marca-login.png"));
        window.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* temporário */ }
    }

    /// <summary>Imagem de exemplo (gradiente) para a miniatura do produto nas capturas.</summary>
    private static byte[] SamplePng(byte r, byte g, byte b, int w = 96, int hgt = 96)
    {
        var px = new byte[w * hgt * 4];
        for (var y = 0; y < hgt; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var t = (x + y) / (double)(w + hgt);
                px[i] = (byte)(b * (1 - t) + 255 * t * 0.3); px[i + 1] = (byte)(g * (1 - t * 0.5)); px[i + 2] = (byte)(r + (255 - r) * t * 0.4); px[i + 3] = 255;
            }
        var src = BitmapSource.Create(w, hgt, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static void Save(Window window, string path)
    {
        for (var i = 0; i < 4; i++) Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

