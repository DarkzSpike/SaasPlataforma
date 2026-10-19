using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

public sealed record ReportRow(string C1, string C2, string C3, string C4, string C5, string Tag = "");

/// <summary>Relatórios gerenciais (curva ABC, giro, vendedores/comissões, horários, DRE) e pacote do contador.</summary>
public sealed class ReportsViewModel : OwnerPage
{
    public static readonly string[] Kinds = ["Curva ABC (produtos que mais faturam)", "Giro de estoque e produtos parados", "Vendedores e comissões", "Horários de pico e ticket médio", "DRE simplificado (resultado)"];
    private string _kind = Kinds[0], _summary = "";
    private string[] _headers = ["", "", "", "", ""];

    public ReportsViewModel(ShellViewModel shell) : base(shell, "Relatórios", "Curva ABC, giro, comissões, horários de pico, DRE e pacote para o contador")
    {
        Period = Periods[2]; // 30 dias
    }

    public override bool HasPeriod => true;
    public IReadOnlyList<string> KindOptions => Kinds;
    public string Kind { get => _kind; set { if (Set(ref _kind, value)) _ = LoadAsync(); } }
    public ObservableCollection<ReportRow> Rows { get; } = [];
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string H1 => _headers[0]; public string H2 => _headers[1]; public string H3 => _headers[2]; public string H4 => _headers[3]; public string H5 => _headers[4];

    public RelayCommand AccountantCommand => new(async _ =>
    {
        var (f, t) = Range();
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"contador-{f:yyyyMMdd}-{t:yyyyMMdd}.zip", Filter = "Arquivo ZIP (*.zip)|*.zip" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bytes = await S.Api.GetBytesAsync($"/api/v1/reports/accountant.zip?from={Q(f)}&to={Q(t)}") ?? [];
            await System.IO.File.WriteAllBytesAsync(dlg.FileName, bytes);
            Summary = "Pacote do contador salvo: " + dlg.FileName;
        }
        catch (ApiException ex) { Error = "Não foi possível gerar: " + ex.Code; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }, _ => S.Session.Can("reports.export"));

    private void Headers(params string[] h)
    {
        _headers = [.. h.Concat(Enumerable.Repeat("", 5)).Take(5)];
        foreach (var n in new[] { nameof(H1), nameof(H2), nameof(H3), nameof(H4), nameof(H5) }) Raise(n);
    }

    private static string M(long c) => AppServices.Money(c);

    protected override async Task LoadCoreAsync()
    {
        var (from, to) = Range();
        var p = $"from={Q(from)}&to={Q(to)}";
        Rows.Clear();
        switch (Array.IndexOf(Kinds, Kind))
        {
            case 0:
            {
                var r = await S.Api.GetAsync("/api/v1/reports/abc?" + p);
                Headers("Classe", "Produto", "Quantidade", "Faturamento", "Acumulado");
                foreach (var x in r.GetProperty("products").EnumerateArray())
                    Rows.Add(new ReportRow(x.GetProperty("class").GetString()!, x.GetProperty("name").GetString()!, x.GetProperty("quantity").GetDecimal().ToString("0.###", AppServices.PtBr),
                        M(x.GetProperty("revenueCents").GetInt64()), $"{x.GetProperty("cumulativePercent").GetDecimal():0.0}%", x.GetProperty("class").GetString()!));
                Summary = $"Faturamento {M(r.GetProperty("totalCents").GetInt64())} • A: {r.GetProperty("a").GetInt32()} produto(s) fazem 80% • B: {r.GetProperty("b").GetInt32()} • C: {r.GetProperty("c").GetInt32()}";
                break;
            }
            case 1:
            {
                var r = await S.Api.GetAsync($"/api/v1/reports/turnover?{p}&storeId={S.Settings.StoreId}");
                Headers("Produto", "Vendido", "Em estoque", "Giro", "Estoque dura");
                foreach (var x in r.GetProperty("products").EnumerateArray())
                {
                    var cover = x.GetProperty("coverageDays").ValueKind == JsonValueKind.Number ? $"{x.GetProperty("coverageDays").GetDecimal():0} dia(s)" : x.GetProperty("stalled").GetBoolean() ? "PARADO" : "—";
                    Rows.Add(new ReportRow(x.GetProperty("name").GetString()!, x.GetProperty("soldQuantity").GetDecimal().ToString("0.###", AppServices.PtBr),
                        x.GetProperty("stockQuantity").GetDecimal().ToString("0.###", AppServices.PtBr),
                        x.GetProperty("turnover").ValueKind == JsonValueKind.Number ? $"{x.GetProperty("turnover").GetDecimal():0.##}x" : "—", cover, x.GetProperty("stalled").GetBoolean() ? "C" : ""));
                }
                Summary = $"Valor do estoque (a custo): {M(r.GetProperty("stockValueCents").GetInt64())} • {r.GetProperty("stalledCount").GetInt32()} produto(s) parado(s) no período";
                break;
            }
            case 2:
            {
                var r = await S.Api.GetAsync("/api/v1/reports/sellers?" + p);
                Headers("Vendedor", "Vendas", "Faturamento", "Ticket médio", "Comissão");
                foreach (var x in r.EnumerateArray())
                    Rows.Add(new ReportRow(x.GetProperty("name").GetString()!, x.GetProperty("sales").GetInt32().ToString(), M(x.GetProperty("revenueCents").GetInt64()),
                        M(x.GetProperty("averageTicketCents").GetInt64()), $"{M(x.GetProperty("commissionCents").GetInt64())} ({x.GetProperty("commissionPercent").GetDecimal():0.##}%)"));
                Summary = "Comissão sobre as vendas líquidas de devoluções. Defina o percentual de cada um em Funcionários.";
                break;
            }
            case 3:
            {
                var r = await S.Api.GetAsync("/api/v1/reports/hours?" + p);
                Headers("Horário", "Vendas", "Faturamento", "", "");
                foreach (var x in r.GetProperty("byHour").EnumerateArray().Where(x => x.GetProperty("sales").GetInt32() > 0))
                    Rows.Add(new ReportRow($"{x.GetProperty("hour").GetInt32():00}h", x.GetProperty("sales").GetInt32().ToString(), M(x.GetProperty("revenueCents").GetInt64()), "", ""));
                foreach (var x in r.GetProperty("byWeekday").EnumerateArray().Where(x => x.GetProperty("sales").GetInt32() > 0))
                    Rows.Add(new ReportRow(x.GetProperty("day").GetString()!, x.GetProperty("sales").GetInt32().ToString(), M(x.GetProperty("revenueCents").GetInt64()), "", "", "B"));
                Summary = $"{r.GetProperty("sales").GetInt32()} venda(s) • ticket médio {M(r.GetProperty("averageTicketCents").GetInt64())}";
                break;
            }
            default:
            {
                var r = await S.Api.GetAsync("/api/v1/reports/dre?" + p);
                Headers("Linha", "Valor", "", "", "");
                void L(string n, long v, string tag = "") => Rows.Add(new ReportRow(n, M(v), "", "", "", tag));
                L("Receita bruta de vendas", r.GetProperty("grossRevenueCents").GetInt64());
                L("(−) Descontos", -r.GetProperty("discountsCents").GetInt64());
                L("(−) Devoluções", -r.GetProperty("returnsCents").GetInt64());
                L("= Receita líquida", r.GetProperty("netRevenueCents").GetInt64(), "A");
                L("(−) Custo das mercadorias vendidas", -r.GetProperty("costOfGoodsCents").GetInt64());
                L($"= Lucro bruto ({r.GetProperty("grossMarginPercent").GetDecimal():0.0}%)", r.GetProperty("grossProfitCents").GetInt64(), "A");
                foreach (var e in r.GetProperty("expenses").EnumerateArray()) L("(−) " + e.GetProperty("category").GetString(), -e.GetProperty("cents").GetInt64());
                L("= Resultado líquido", r.GetProperty("netResultCents").GetInt64(), r.GetProperty("netResultCents").GetInt64() >= 0 ? "A" : "C");
                Summary = r.GetProperty("note").GetString() ?? "";
                break;
            }
        }
    }
}
