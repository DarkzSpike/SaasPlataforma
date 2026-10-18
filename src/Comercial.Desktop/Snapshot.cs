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
                Permissions = ["sales.read", "sales.cancel", "reports.read", "reports.financial", "stock.read", "cash.review", "cash.operate", "sales.create"],
                Modules = ["pdv", "estoque", "caixa", "app_proprietario", "lucratividade", "white_label"],
            },
        };
        var main = new MainViewModel(services);
        var window = new MainWindow(main) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, Width = 1600, Height = 980, ShowActivated = false, ShowInTaskbar = false };
        window.Show();

        var login = (LoginViewModel)main.Current;
        login.TenantCode = "loja-demo";
        login.Email = "dona@loja-demo.test";
        Save(window, Path.Combine(dir, "01-login.png"));
        login.Status = "Usuário ou senha inválidos.";
        Save(window, Path.Combine(dir, "02-login-erro.png"));

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

        var dash = new DashboardViewModel(shell);
        dash.Kpis.Add(new Kpi("Vendas hoje", "R$ 1.284,90", "37 venda(s)", "", "accent"));
        dash.Kpis.Add(new Kpi("Lucro bruto hoje", "R$ 602,15", "estimado (sem despesas)", "", "primary"));
        dash.Kpis.Add(new Kpi("Vendas no mês", "R$ 21.940,30", "612 venda(s)", "", "accent"));
        dash.Kpis.Add(new Kpi("Lucro bruto no mês", "R$ 10.112,80", "estimado (sem despesas)", "", "primary"));
        dash.Kpis.Add(new Kpi("Caixas abertos", "1", "agora", "", "neutral"));
        dash.Kpis.Add(new Kpi("Estoque baixo", "2", "produto(s) para repor", "", "warn"));
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
        stock.SetRows([
            new StockRow("Toalha de rosto algodão", "TOA004", "3", "5", true),
            new StockRow("Caixa organizadora 20 L", "ORG006", "4", "5", true),
            new StockRow("Caneca de cerâmica 300 ml", "CAN001", "38", "5", false),
            new StockRow("Caderno universitário 10 matérias", "CAD002", "25", "5", false),
            new StockRow("Copo de vidro 350 ml", "COP005", "118", "5", false),
            new StockRow("Pilha alcalina AA (par)", "PIL003", "57", "5", false)]);
        shell.ShowForPreview("stock", stock);
        Save(window, Path.Combine(dir, "08-estoque.png"));

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

        window.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* temporário */ }
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
