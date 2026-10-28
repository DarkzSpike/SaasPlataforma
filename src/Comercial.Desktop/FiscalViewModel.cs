using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

public sealed record FiscalDocRow(Guid Id, string When, string Number, string Series, string Model, string Recipient, string Total, string Status, string StatusKey, int StatusCode,
    string Message, string? DanfeUrl, string Key, string Protocol, bool HasXml, bool HasCancelXml)
{
    public bool CanTransmit => StatusCode is 1 or 3 or 4;
    public bool CanCancel => StatusCode == 2;
    public string Xml => HasXml ? "guardado" : StatusCode is 2 or 5 or 6 && StatusKey != "descartada" ? "FALTA" : "—";
}

public sealed record SectionItem(string Key, string Label, string Glyph);
public sealed record CheckRow(string Title, string State, string StateLabel, string Detail, string Items, bool HasItems);
public sealed record ExportRow(Guid Id, string When, string Period, string Docs, string Events, string Voids, string Missing, string By, string Size, string File, int MissingCount);
public sealed record DeliveryRow(Guid? ExportId, string When, string Period, string Trigger, string Status, string StatusLabel, string Recipients, string Detail, string By);
public sealed record StoreOption(Guid? Id, string Name) { public override string ToString() => Name; }
public sealed record NamedOption(string Key, string Label) { public override string ToString() => Label; }

/// <summary>
/// Fiscal e Contabilidade: visão geral, documentos (NFC-e), exportação dos XMLs, relatórios, fechamento mensal, envio ao contador e configurações.
/// </summary>
public sealed class FiscalViewModel : OwnerPage
{
    public FiscalViewModel(ShellViewModel shell) : base(shell, "Fiscal e Contabilidade", "Notas fiscais, XMLs, relatórios, fechamento do mês e envio ao contador")
    {
        var today = DateTime.Today;
        Years = Enumerable.Range(today.Year - 4, 5).Reverse().ToList();
        _year = today.Year; _month = Months[today.Month - 1];
        _section = Sections[0];
        _status = StatusOptions[0];
        _report = ReportOptions[0];
        _store = Stores[0];
    }

    // ---------------- Navegação e período ----------------

    public IReadOnlyList<SectionItem> Sections { get; } =
    [
        new("geral", "Visão geral", ""), new("documentos", "Documentos", ""), new("exportacao", "Exportar XMLs", ""),
        new("relatorios", "Relatórios", ""), new("fechamento", "Fechamento", ""), new("contador", "Contador", ""), new("config", "Configurações", ""),
    ];
    private SectionItem _section;
    public SectionItem Section
    {
        get => _section;
        set { if (value is not null && Set(ref _section, value)) { foreach (var s in Sections) Raise("Is" + Cap(s.Key)); Info = ""; _ = LoadAsync(); } }
    }
    private static string Cap(string k) => char.ToUpperInvariant(k[0]) + k[1..];
    public bool IsGeral => Section.Key == "geral";
    public bool IsDocumentos => Section.Key == "documentos";
    public bool IsExportacao => Section.Key == "exportacao";
    public bool IsRelatorios => Section.Key == "relatorios";
    public bool IsFechamento => Section.Key == "fechamento";
    public bool IsContador => Section.Key == "contador";
    public bool IsConfig => Section.Key == "config";

    public IReadOnlyList<int> Years { get; }
    public IReadOnlyList<NamedOption> Months { get; } = Enumerable.Range(1, 12)
        .Select(m => new NamedOption(m.ToString(), System.Globalization.CultureInfo.GetCultureInfo("pt-BR").DateTimeFormat.GetMonthName(m) is var n ? char.ToUpper(n[0]) + n[1..] : "")).ToList();
    private int _year;
    private NamedOption _month;
    public int Year { get => _year; set { if (Set(ref _year, value)) { _page = 1; _ = LoadAsync(); } } }
    public NamedOption Month { get => _month; set { if (value is not null && Set(ref _month, value)) { _page = 1; _ = LoadAsync(); } } }
    private int MonthNumber => int.Parse(Month.Key);
    private string From => $"{Year}-{MonthNumber:00}-01";
    private string To => new DateOnly(Year, MonthNumber, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
    private string PeriodLabel => $"{Month.Label}/{Year}";

    public ObservableCollection<StoreOption> Stores { get; } = [new(null, "Todas as lojas")];
    private StoreOption? _store;
    public StoreOption? Store { get => _store; set { if (Set(ref _store, value)) { _page = 1; _ = LoadAsync(); } } }
    private string StoreQ => Store?.Id is { } id ? "&storeId=" + id : "";

    private string _info = "", _busy = "";
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;
    /// <summary>Mensagem de progresso (gerando pacote, enviando…).</summary>
    public string Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(IsBusy)); } }
    public bool IsBusy => Busy.Length > 0;

    public bool CanConfigure => S.Session.Can("payments.configure");
    public bool CanExportXml => S.Session.Can("fiscal.export", "fiscal");
    public bool CanClose => S.Session.Can("fiscal.close", "fiscal");
    public bool CanSend => S.Session.Can("fiscal.send", "fiscal");

    private async Task Do(string busy, Func<Task> a)
    {
        Error = ""; Info = ""; Busy = busy;
        try { await a(); }
        catch (ApiException ex) { Error = Friendly(ex.Code, ex.Message); }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
        finally { Busy = ""; }
    }

    private static string Friendly(string code, string message) => code switch
    {
        "fiscal_cancel_window" => "Prazo de cancelamento (30 min) encerrado. Fale com o contador.",
        "fiscal_not_configured" => "Configure o emitente e o token do provedor fiscal.",
        "reason_required" => "Justificativa: de 15 a 255 caracteres (reabertura: mínimo 10).",
        "xml_not_archived" => "O XML original desta nota ainda não está guardado. Use \"Buscar XMLs no provedor\".",
        "closing_needs_notes" => "Há verificações não concluídas: escreva nas observações como foram tratadas (mín. 10 caracteres).",
        "already_closed" => "Este mês já está fechado.",
        "accountant_not_configured" => "Cadastre o escritório de contabilidade na aba Contador.",
        "delivery_in_progress" => "Este pacote já está na fila de envio. Aguarde o resultado.",
        "export_too_big" or "export_file_missing" or "export_integrity" or "xml_integrity" => message,
        "invalid_email" or "invalid_day" or "invalid_hour" or "invalid_office" or "too_many_emails" => message,
        "module_not_enabled" => "O módulo fiscal não está contratado para a sua empresa.",
        "forbidden" or "permission_denied" => "Seu usuário não tem permissão para esta ação.",
        _ => "Não foi possível: " + code,
    };

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static long Cents(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    private static string When(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset().LocalDateTime.ToString("dd/MM/yyyy HH:mm") : "—";

    private async Task<bool> SaveAsync(string path, string fileName, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = filter };
        if (dlg.ShowDialog() != true) return false;
        var bytes = await S.Api.GetBytesAsync(path) ?? [];
        await System.IO.File.WriteAllBytesAsync(dlg.FileName, bytes);
        Info = "Arquivo salvo: " + dlg.FileName;
        return true;
    }

    protected override async Task LoadCoreAsync()
    {
        if (Stores.Count == 1)
            try { foreach (var s in (await S.Api.GetAsync("/api/v1/stores")).EnumerateArray()) Stores.Add(new(s.GetProperty("id").GetGuid(), Str(s, "name") ?? "Loja")); }
            catch (ApiException) { /* sem permissão para listar lojas: fica "todas" */ }
        switch (Section.Key)
        {
            case "geral": await LoadOverviewAsync(); break;
            case "documentos": await LoadDocsAsync(); break;
            case "exportacao": await LoadExportsAsync(); break;
            case "relatorios": await LoadReportAsync(); break;
            case "fechamento": await LoadClosingAsync(); break;
            case "contador": await LoadAccountantAsync(); break;
            case "config": await LoadConfigAsync(); break;
        }
    }

    // ---------------- Visão geral ----------------

    public ObservableCollection<Kpi> Kpis { get; } = [];
    public ObservableCollection<string> Pendencies { get; } = [];
    public ObservableCollection<string> PaymentLines { get; } = [];
    private string _overviewStatus = "";
    public string OverviewStatus { get => _overviewStatus; set => Set(ref _overviewStatus, value); }

    private async Task LoadOverviewAsync()
    {
        var o = await S.Api.GetAsync($"/api/v1/fiscal/overview?from={From}&to={To}{StoreQ}");
        Kpis.Clear();
        Kpis.Add(new Kpi("NFC-e autorizadas", Int(o, "authorized").ToString(), AppServices.Money(Cents(o, "authorizedCents")), "", "primary"));
        Kpis.Add(new Kpi("Canceladas", Int(o, "cancelled").ToString(), AppServices.Money(Cents(o, "cancelledCents")), "", Int(o, "cancelled") > 0 ? "warn" : "neutral"));
        Kpis.Add(new Kpi("Rejeitadas/denegadas", (Int(o, "rejected") + Int(o, "denied")).ToString(), "precisam de análise", "", Int(o, "rejected") + Int(o, "denied") > 0 ? "danger" : "neutral"));
        Kpis.Add(new Kpi("Pendentes", (Int(o, "pending") + Int(o, "missingXml")).ToString(), $"{Int(o, "pending")} sem autorizar • {Int(o, "missingXml")} sem XML", "",
            Int(o, "pending") + Int(o, "missingXml") > 0 ? "warn" : "neutral"));
        Kpis.Add(new Kpi("Vendas do período", AppServices.Money(Cents(o, "salesCents")), $"{Int(o, "salesCount")} venda(s)", "", "accent"));
        Pendencies.Clear();
        foreach (var p in o.GetProperty("pendencies").EnumerateArray()) Pendencies.Add("• " + p.GetString());
        if (Pendencies.Count == 0) Pendencies.Add("Nenhuma pendência no período.");
        PaymentLines.Clear();
        foreach (var p in o.GetProperty("payments").EnumerateArray()) PaymentLines.Add($"{Str(p, "method")}: {AppServices.Money(Cents(p, "cents"))}");
        var last = o.GetProperty("lastDelivery");
        OverviewStatus = $"Fechamento de {PeriodLabel}: {Str(o, "closing")}" +
            (o.GetProperty("fiscalEnabled").GetBoolean() ? "" : " • módulo fiscal desligado") +
            (last.ValueKind == JsonValueKind.Object ? $"\nÚltimo envio ao contador: {When(last, "createdAt")} ({Int(last, "month"):00}/{Int(last, "year")}) — {DeliveryLabel(Str(last, "status") ?? "")}" : "\nNenhum envio ao contador ainda.");
    }

    // ---------------- Documentos ----------------

    public ObservableCollection<FiscalDocRow> Docs { get; } = [];
    private FiscalDocRow? _selected;
    public FiscalDocRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public IReadOnlyList<NamedOption> StatusOptions { get; } =
    [
        new("todas", "Todas as situações"), new("autorizada", "Autorizadas"), new("cancelada", "Canceladas"), new("rejeitada", "Rejeitadas"), new("denegada", "Denegadas"),
        new("pendente", "Pendentes"), new("contingencia", "Contingência"), new("descartada", "Descartadas (não transmitidas)"),
    ];
    private NamedOption _status;
    public NamedOption StatusFilter { get => _status; set { if (value is not null && Set(ref _status, value)) { _page = 1; _ = LoadAsync(); } } }
    public string NumberFilter { get; set; } = "";
    public string SeriesFilter { get; set; } = "";
    public string KeyFilter { get; set; } = "";
    private int _page = 1, _total;
    private string _pageInfo = "";
    public string PageInfo { get => _pageInfo; set => Set(ref _pageInfo, value); }
    public RelayCommand SearchCommand => new(async _ => { _page = 1; await LoadAsync(); });
    public RelayCommand PrevPageCommand => new(async _ => { _page--; await LoadAsync(); }, _ => _page > 1);
    public RelayCommand NextPageCommand => new(async _ => { _page++; await LoadAsync(); }, _ => _page * 50 < _total);

    private static string StatusName(string k) => k switch
    {
        "autorizada" => "Autorizada", "cancelada" => "Cancelada", "rejeitada" => "Rejeitada", "denegada" => "Denegada", "pendente" => "Pendente",
        "contingencia" => "Contingência", "descartada" => "Descartada (não transmitida)", _ => k,
    };

    private async Task LoadDocsAsync()
    {
        var q = $"/api/v1/fiscal/docs?from={From}&to={To}{StoreQ}&page={_page}&pageSize=50&status={StatusFilter.Key}";
        if (int.TryParse(NumberFilter.Trim(), out var n)) q += "&number=" + n;
        if (int.TryParse(SeriesFilter.Trim(), out var se)) q += "&series=" + se;
        if (KeyFilter.Trim().Length > 0) q += "&key=" + Uri.EscapeDataString(KeyFilter.Trim());
        var r = await S.Api.GetAsync(q);
        _total = r.GetProperty("total").GetInt32();
        Docs.Clear();
        foreach (var d in r.GetProperty("items").EnumerateArray())
        {
            var key = Str(d, "accessKey") ?? "";
            var st = Str(d, "status") ?? "";
            Docs.Add(new FiscalDocRow(d.GetProperty("id").GetGuid(), When(d, "issuedAt"), Int(d, "number") is > 0 and var num ? num.ToString() : "—",
                Int(d, "series") is > 0 and var ser ? ser.ToString() : "—", Int(d, "model") == 55 ? "NF-e 55" : "NFC-e 65", Str(d, "recipient") ?? "",
                AppServices.Money(Cents(d, "totalCents")), StatusName(st), st, d.GetProperty("statusCode").GetInt32(), Str(d, "lastMessage") ?? "", Str(d, "danfeUrl"),
                key, Str(d, "protocol") ?? "", d.GetProperty("hasXml").GetBoolean(), d.GetProperty("hasCancelXml").GetBoolean()));
        }
        var pages = Math.Max(1, (_total + 49) / 50);
        PageInfo = $"{_total} documento(s) • página {_page} de {pages}";
    }

    public RelayCommand DetailCommand => RelayCommand.Sync(_ => _ = Do("Carregando detalhes…", async () =>
    {
        var d = await S.Api.GetAsync($"/api/v1/fiscal/docs/{Selected!.Id}");
        var sale = d.GetProperty("sale");
        var lines = sale.GetProperty("items").EnumerateArray().Select(i => $"{i.GetProperty("quantity").GetDecimal():0.###} × {Str(i, "productName")} = {AppServices.Money(Cents(i, "totalCents"))}");
        var pays = sale.GetProperty("payments").EnumerateArray().Select(p => $"{Str(p, "method")} {AppServices.Money(Cents(p, "amountCents"))}");
        var events = d.GetProperty("events").EnumerateArray().Select(e => $"{When(e, "createdAt")} {Str(e, "type")}: {Str(e, "message")}");
        var files = d.GetProperty("files").EnumerateArray().Select(f => $"{Str(f, "kind")} • SHA-256 {Str(f, "sha256")?[..16]}… • {Int(f, "sizeBytes")} bytes");
        Shell.ShowPrompt(new PromptViewModel
        {
            Title = $"{Selected.Model} {Selected.Number}/{Selected.Series} — {Selected.Status}",
            Message = $"Chave: {(Str(d, "accessKey") ?? "—")}\nProtocolo: {(Str(d, "protocol") ?? "—")}\nAmbiente: {(Int(d, "environment") == 1 ? "produção" : "homologação (sem valor fiscal)")}\n" +
                      $"Emitida em: {When(sale, "receivedAt")} • Total: {AppServices.Money(Cents(sale, "totalCents"))}\n\nItens:\n{string.Join("\n", lines)}\n\nPagamentos: {string.Join(", ", pays)}\n\n" +
                      $"Arquivo fiscal:\n{(files.Any() ? string.Join("\n", files) : "nenhum XML guardado ainda")}\n\nHistórico:\n{string.Join("\n", events)}",
            ConfirmText = "Fechar",
        }, _ => Task.FromResult(true));
    }), _ => Selected is not null);

    public RelayCommand DownloadXmlCommand => new(async _ => await Do("Baixando XML…", () =>
        SaveAsync($"/api/v1/fiscal/docs/{Selected!.Id}/xml", $"{(Selected.Key.Length > 0 ? Selected.Key : Selected.Id.ToString("N"))}-procNFe.xml", "XML (*.xml)|*.xml")),
        _ => Selected is { HasXml: true } && CanExportXml);

    public RelayCommand DownloadCancelXmlCommand => new(async _ => await Do("Baixando XML…", () =>
        SaveAsync($"/api/v1/fiscal/docs/{Selected!.Id}/xml?kind=cancelamento", $"{Selected.Key}-cancelamento.xml", "XML (*.xml)|*.xml")),
        _ => Selected is { HasCancelXml: true } && CanExportXml);

    public RelayCommand FetchXmlCommand => new(async _ => await Do("Buscando XMLs no provedor fiscal…", async () =>
    {
        var r = await S.Api.PostAsync("/api/v1/fiscal/archive/fetch", new { });
        await LoadAsync();
        Info = $"{Int(r, "stored")} documento(s) com XML original guardado agora. Os que continuarem faltando aparecem como pendência.";
    }), _ => CanExportXml);

    public RelayCommand TransmitCommand => new(async _ => await Do("Transmitindo…", async () =>
    {
        var r = await S.Api.PostAsync($"/api/v1/fiscal/documents/{Selected!.Id}/transmit", new { });
        await LoadAsync();
        Info = $"Situação: {Str(r, "lastMessage")}";
    }), _ => Selected is { CanTransmit: true });

    public RelayCommand CancelCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Cancelar NFC-e " + Selected!.Number, Message = "Só é possível em até 30 minutos da autorização. A justificativa vai para a SEFAZ.",
        TextLabel = "Justificativa (15 a 255 caracteres)", ConfirmText = "Cancelar NFC-e",
    }, async p =>
    {
        try { await S.Api.PostAsync($"/api/v1/fiscal/documents/{Selected!.Id}/cancel", new { reason = p.Text.Trim() }); await LoadAsync(); Info = "NFC-e cancelada. O XML do evento é guardado automaticamente."; return true; }
        catch (ApiException ex) { p.Error = Friendly(ex.Code, ex.Message); }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => Selected is { CanCancel: true });

    public RelayCommand OpenDanfeCommand => RelayCommand.Sync(_ =>
    {
        if (Selected?.DanfeUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }, _ => Selected?.DanfeUrl is not null);

    // ---------------- Exportação ----------------

    public ObservableCollection<ExportRow> Exports { get; } = [];
    private ExportRow? _selectedExport;
    public ExportRow? SelectedExport { get => _selectedExport; set => Set(ref _selectedExport, value); }
    public bool IncludeReports { get; set; } = true;
    private string _exportSummary = "";
    public string ExportSummary { get => _exportSummary; set => Set(ref _exportSummary, value); }

    private static string Size(long b) => b >= 1024 * 1024 ? $"{b / 1024d / 1024:0.0} MB" : $"{Math.Max(1, b / 1024)} KB";

    private async Task LoadExportsAsync()
    {
        Exports.Clear();
        foreach (var e in (await S.Api.GetAsync($"/api/v1/fiscal/exports?year={Year}&month={MonthNumber}")).EnumerateArray())
            Exports.Add(new ExportRow(e.GetProperty("id").GetGuid(), When(e, "createdAt"), $"{Int(e, "month"):00}/{Int(e, "year")}", Int(e, "documentCount").ToString(),
                Int(e, "eventCount").ToString(), Int(e, "voidCount").ToString(), Int(e, "missingCount").ToString(), Str(e, "createdBy") ?? "", Size(Cents(e, "sizeBytes")),
                Str(e, "fileName") ?? "", Int(e, "missingCount")));
        ExportSummary = Exports.Count == 0
            ? $"Nenhum pacote de {PeriodLabel} ainda. O pacote junta os XMLs originais (autorizadas, cancelamentos, inutilizações), os relatórios em CSV, um resumo e o manifesto SHA-256."
            : $"{Exports.Count} pacote(s) de {PeriodLabel}. Pacotes anteriores ficam guardados e podem ser baixados de novo.";
    }

    public RelayCommand CreateExportCommand => new(async _ => await Do($"Gerando o pacote de {PeriodLabel}… (pode levar alguns segundos)", async () =>
    {
        var r = await S.Api.PostAsync("/api/v1/fiscal/exports", new { year = Year, month = MonthNumber, storeId = Store?.Id, includeReports = IncludeReports });
        await LoadExportsAsync();
        var e = r.GetProperty("export");
        Info = (r.GetProperty("reused").GetBoolean() ? "Nada mudou desde o último pacote: o mesmo arquivo foi reaproveitado. " : "Pacote gerado. ") +
               $"{Int(e, "documentCount")} NFC-e, {Int(e, "eventCount")} evento(s), {Int(e, "voidCount")} inutilização(ões)" +
               (Int(e, "missingCount") > 0 ? $" — ATENÇÃO: {Int(e, "missingCount")} pendência(s) listadas em pendencias.csv." : ".");
        SelectedExport = Exports.FirstOrDefault(x => x.Id == e.GetProperty("id").GetGuid());
    }), _ => CanExportXml && !IsBusy);

    public RelayCommand DownloadExportCommand => new(async _ => await Do("Baixando pacote…", () =>
        SaveAsync($"/api/v1/fiscal/exports/{SelectedExport!.Id}/download", SelectedExport.File, "Arquivo ZIP (*.zip)|*.zip")), _ => SelectedExport is not null && CanExportXml);

    public RelayCommand SendExportCommand => RelayCommand.Sync(_ => ConfirmSend(SelectedExport!), _ => SelectedExport is not null && CanSend);

    private void ConfirmSend(ExportRow e) => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Enviar para o contador",
        Message = $"Enviar o pacote de {e.Period} ({e.Docs} NFC-e, {e.Events} evento(s), {e.Size}) para o e-mail cadastrado do contador?" +
                  (e.MissingCount > 0 ? $"\n\nAtenção: o pacote tem {e.Missing} pendência(s)." : ""),
        ConfirmText = "Enviar",
    }, async p =>
    {
        try
        {
            var r = await S.Api.PostAsync("/api/v1/fiscal/accountant/send", new { exportId = e.Id });
            Info = $"Envio colocado na fila para {Str(r, "recipients")}. Acompanhe o resultado na aba Contador.";
            return true;
        }
        catch (ApiException ex) { p.Error = Friendly(ex.Code, ex.Message); }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    });

    // ---------------- Relatórios ----------------

    public IReadOnlyList<NamedOption> ReportOptions { get; } =
    [
        new("caixa", "Fechamento de caixa"), new("pagamentos", "Vendas por meio de pagamento"), new("conciliacao", "Conciliação de pagamentos (PIX)"),
        new("vendas_fiscal", "Vendas e documentos fiscais"), new("divergencias", "Divergências para análise"), new("cancelamentos", "Cancelamentos"),
        new("devolucoes", "Devoluções"), new("rejeitados", "Documentos rejeitados"), new("pendentes", "Documentos pendentes"), new("produtos", "Vendas por produto"),
        new("financeiro", "Movimentações financeiras"),
    ];
    private NamedOption _report;
    public NamedOption Report { get => _report; set { if (value is not null && Set(ref _report, value)) _ = LoadAsync(); } }
    private DataView? _reportView;
    public DataView? ReportView { get => _reportView; set => Set(ref _reportView, value); }
    public ObservableCollection<string> ReportTotals { get; } = [];
    private string _reportNotes = "";
    public string ReportNotes { get => _reportNotes; set => Set(ref _reportNotes, value); }

    private static string Col(string h)
    {
        var s = new string(h.Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : ' ').ToArray()).Trim();
        return s.Length == 0 ? "col" : char.ToUpper(s[0]) + s[1..];
    }

    private async Task LoadReportAsync()
    {
        var t = await S.Api.GetAsync($"/api/v1/fiscal/reports/{Report.Key}?from={From}&to={To}{StoreQ}");
        var table = new DataTable();
        foreach (var h in t.GetProperty("headers").EnumerateArray())
        {
            var name = Col(h.GetString()!.Replace('_', ' '));
            while (table.Columns.Contains(name)) name += " ";
            table.Columns.Add(name);
        }
        foreach (var r in t.GetProperty("rows").EnumerateArray()) table.Rows.Add(r.EnumerateArray().Select(c => (object)(c.GetString() ?? "")).ToArray());
        ReportView = table.DefaultView;
        ReportTotals.Clear();
        foreach (var x in t.GetProperty("totals").EnumerateArray()) ReportTotals.Add($"{Str(x, "label")}: {Str(x, "value")}");
        ReportNotes = string.Join("\n", t.GetProperty("notes").EnumerateArray().Select(n => "• " + n.GetString()));
        if (table.Rows.Count == 0) ReportTotals.Insert(0, "Nenhum registro no período.");
    }

    public RelayCommand ReportCsvCommand => new(async _ => await Do("Exportando…", () =>
        SaveAsync($"/api/v1/fiscal/reports/{Report.Key}?from={From}&to={To}{StoreQ}&format=csv", $"{Report.Key}-{Year}-{MonthNumber:00}.csv", "Planilha CSV (*.csv)|*.csv")));

    // ---------------- Fechamento ----------------

    public ObservableCollection<CheckRow> Checks { get; } = [];
    private string _closingStatus = "", _closingSummary = "", _closingNotes = "";
    private bool _isClosed;
    public string ClosingStatus { get => _closingStatus; set => Set(ref _closingStatus, value); }
    public string ClosingSummary { get => _closingSummary; set => Set(ref _closingSummary, value); }
    public string ClosingNotes { get => _closingNotes; set => Set(ref _closingNotes, value); }
    public bool IsClosed { get => _isClosed; set => Set(ref _isClosed, value); }

    private static string StateLabel(string s) => s switch
    {
        "pendente" => "Pendente", "em_analise" => "Em análise", "concluido" => "Concluído", "divergencia" => "Com divergência", "nao_aplicavel" => "Não aplicável", _ => s,
    };

    private void ShowClosing(JsonElement c)
    {
        Checks.Clear();
        foreach (var x in c.GetProperty("checks").EnumerateArray())
        {
            var items = x.GetProperty("items").EnumerateArray().Select(i => "   – " + i.GetString()).ToList();
            Checks.Add(new CheckRow(Str(x, "title") ?? "", Str(x, "state") ?? "", StateLabel(Str(x, "state") ?? ""), Str(x, "detail") ?? "", string.Join("\n", items), items.Count > 0));
        }
        var status = Str(c, "status");
        IsClosed = status == "concluido";
        ClosingStatus = status switch
        {
            "concluido" => $"Mês FECHADO em {When(c, "closedAt")} por {Str(c, "closedBy") ?? "?"}." + (Str(c, "notes") is { } n ? "\nObservações: " + n : ""),
            "aberto" => "Fechamento em andamento (reaberto).",
            _ => "Fechamento ainda não concluído. Resolva as pendências (ou registre nas observações como foram tratadas) e conclua.",
        };
        ClosingSummary = Str(c, "summary") ?? "";
    }

    private async Task LoadClosingAsync() => ShowClosing(await S.Api.GetAsync($"/api/v1/fiscal/closing/{Year}/{MonthNumber}"));

    public RelayCommand CloseMonthCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = $"Concluir fechamento de {PeriodLabel}",
        Message = "O fechamento NÃO altera, cancela ou apaga documentos fiscais. Ele registra as verificações, quem fechou e as observações.",
        ConfirmText = "Concluir fechamento",
    }, async p =>
    {
        try { ShowClosing(await S.Api.PostAsync($"/api/v1/fiscal/closing/{Year}/{MonthNumber}/close", new { notes = ClosingNotes })); Info = "Fechamento concluído."; return true; }
        catch (ApiException ex) { p.Error = Friendly(ex.Code, ex.Message); }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => CanClose && !IsClosed);

    public RelayCommand ReopenMonthCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = $"Reabrir {PeriodLabel}", Message = "A reabertura fica registrada na auditoria.", TextLabel = "Motivo (mín. 10 caracteres)", ConfirmText = "Reabrir",
    }, async p =>
    {
        try { ShowClosing(await S.Api.PostAsync($"/api/v1/fiscal/closing/{Year}/{MonthNumber}/reopen", new { notes = p.Text.Trim() })); Info = "Mês reaberto."; return true; }
        catch (ApiException ex) { p.Error = Friendly(ex.Code, ex.Message); }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => CanClose && IsClosed);

    public RelayCommand SaveSummaryCommand => RelayCommand.Sync(_ =>
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"fechamento-{Year}-{MonthNumber:00}.txt", Filter = "Texto (*.txt)|*.txt" };
        if (dlg.ShowDialog() == true) { System.IO.File.WriteAllText(dlg.FileName, ClosingSummary); Info = "Resumo salvo: " + dlg.FileName; }
    });

    // ---------------- Contador ----------------

    public ObservableCollection<DeliveryRow> Deliveries { get; } = [];
    private DeliveryRow? _selectedDelivery;
    public DeliveryRow? SelectedDelivery { get => _selectedDelivery; set => Set(ref _selectedDelivery, value); }
    private string _accountantSummary = "";
    public string AccountantSummary { get => _accountantSummary; set => Set(ref _accountantSummary, value); }
    private JsonElement _accountant;

    private static string DeliveryLabel(string s) => s switch
    {
        "na_fila" => "Na fila de envio", "aceito_pelo_servidor" => "Aceito pelo servidor de e-mail", "falhou" => "Falhou", "bloqueado" => "Bloqueado (pendências)", _ => s,
    };

    private async Task LoadAccountantAsync()
    {
        _accountant = await S.Api.GetAsync("/api/v1/fiscal/accountant");
        AccountantSummary = _accountant.TryGetProperty("configured", out var c) && c.GetBoolean()
            ? $"{Str(_accountant, "officeName")} — {Str(_accountant, "contactName")}\nE-mail: {Str(_accountant, "email")}" +
              (Str(_accountant, "extraEmails") is { Length: > 0 } x ? $" (+ {x})" : "") +
              (_accountant.GetProperty("autoSend").GetBoolean()
                  ? $"\nEnvio automático: todo dia {Int(_accountant, "dayOfMonth")} às {Int(_accountant, "hour")}h, com o pacote do mês anterior (só se as verificações essenciais estiverem em dia)."
                  : "\nEnvio automático desligado: envie manualmente pela aba Exportar XMLs ou aqui.")
            : "Escritório de contabilidade não cadastrado. Clique em \"Configurar escritório\".";
        Deliveries.Clear();
        foreach (var d in (await S.Api.GetAsync("/api/v1/fiscal/accountant/deliveries")).EnumerateArray())
            Deliveries.Add(new DeliveryRow(d.TryGetProperty("fiscalExportId", out var e) && e.ValueKind == JsonValueKind.String ? e.GetGuid() : null, When(d, "createdAt"),
                $"{Int(d, "month"):00}/{Int(d, "year")}", Str(d, "trigger") switch { "auto" => "automático", "reenvio" => "reenvio", _ => "manual" },
                Str(d, "status") ?? "", DeliveryLabel(Str(d, "status") ?? ""), Str(d, "recipients") ?? "", Str(d, "detail") ?? "", Str(d, "by") ?? ""));
    }

    public RelayCommand ConfigureAccountantCommand => RelayCommand.Sync(_ =>
    {
        var a = _accountant;
        string P(string n, string d = "") => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : d;
        var f = new FormViewModel { Title = "Escritório de contabilidade", Message = "O pacote é enviado por e-mail (anexo ZIP até 15 MB) pelo servidor de e-mail do sistema.", ConfirmText = "Salvar" };
        f.Fields.Add(new FormField("office", "Nome do escritório", P("officeName"), Icons.Edit));
        f.Fields.Add(new FormField("contact", "Contador responsável", P("contactName"), Icons.Edit));
        f.Fields.Add(new FormField("email", "E-mail de destino", P("email"), Icons.Edit));
        f.Fields.Add(new FormField("extra", "E-mails adicionais autorizados", P("extraEmails"), Icons.Edit, "separe por vírgula (até 5)"));
        f.Fields.Add(new FormField("auto", "Envio automático mensal", P("autoSend", "False") == "True" ? "Ligado" : "Desligado", options: ["Ligado", "Desligado"]));
        f.Fields.Add(new FormField("day", "Dia do envio (1 a 28)", P("dayOfMonth", "5"), Icons.Edit));
        f.Fields.Add(new FormField("hour", "Hora do envio (0 a 23)", P("hour", "8"), Icons.Edit));
        f.Fields.Add(new FormField("reports", "Incluir relatórios no pacote", P("includeReports", "True") == "False" ? "Não" : "Sim", options: ["Sim", "Não"]));
        Shell.ShowForm(f, async form =>
        {
            try
            {
                await S.Api.PutAsync("/api/v1/fiscal/accountant", new
                {
                    officeName = form["office"], contactName = form["contact"], email = form["email"], extraEmails = form["extra"], autoSend = form["auto"] == "Ligado",
                    dayOfMonth = int.TryParse(form["day"], out var dd) ? dd : 0, hour = int.TryParse(form["hour"], out var hh) ? hh : -1, includeXml = true, includeReports = form["reports"] == "Sim",
                });
                await LoadAsync();
                Info = "Dados do contador salvos.";
                return true;
            }
            catch (ApiException ex) { form.Error = Friendly(ex.Code, ex.Message); }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => CanSend);

    /// <summary>Gera (ou reaproveita) o pacote do mês selecionado e envia.</summary>
    public RelayCommand SendMonthCommand => new(async _ => await Do($"Preparando o pacote de {PeriodLabel}…", async () =>
    {
        var r = await S.Api.PostAsync("/api/v1/fiscal/exports", new { year = Year, month = MonthNumber, storeId = (Guid?)null, includeReports = true });
        var e = r.GetProperty("export");
        ConfirmSend(new ExportRow(e.GetProperty("id").GetGuid(), When(e, "createdAt"), PeriodLabel, Int(e, "documentCount").ToString(), Int(e, "eventCount").ToString(),
            Int(e, "voidCount").ToString(), Int(e, "missingCount").ToString(), "", Size(Cents(e, "sizeBytes")), Str(e, "fileName") ?? "", Int(e, "missingCount")));
    }), _ => CanSend && CanExportXml && !IsBusy);

    public RelayCommand ResendCommand => new(async _ => await Do("Reenviando…", async () =>
    {
        var r = await S.Api.PostAsync("/api/v1/fiscal/accountant/send", new { exportId = SelectedDelivery!.ExportId });
        await LoadAsync();
        Info = $"Reenvio colocado na fila para {Str(r, "recipients")} (mesmo pacote, sem gerar documentos de novo).";
    }), _ => SelectedDelivery is { ExportId: not null } && CanSend);

    // ---------------- Configurações (emitente NFC-e) ----------------

    private string _summary = "";
    private JsonElement _profile;
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    private static readonly string[] Regimes = ["Simples Nacional", "Simples Nacional - excesso de sublimite", "Regime normal", "MEI"];
    private static readonly string[] Envs = ["Homologação (testes, sem valor fiscal)", "Produção (valor fiscal)"];

    private static readonly string[] Modes = ["Direto na SEFAZ (sem custo por nota)", "Focus NFe (provedor pago)"];
    private static readonly string[] Emission = ["Sempre emitir NFC-e", "Só quando o cliente pedir", "Não emitir (só comprovante simples)"];
    private static readonly string[] EmissionKeys = ["sempre", "a_pedido", "nunca"];

    private async Task LoadConfigAsync()
    {
        _profile = await S.Api.GetAsync("/api/v1/fiscal/profile");
        var configured = _profile.TryGetProperty("configured", out var c) && c.GetBoolean();
        var direct = Str(_profile, "provider") == "sefaz";
        var cert = _profile.TryGetProperty("hasCertificate", out var hc) && hc.GetBoolean()
            ? $"\nCertificado A1: {Str(_profile, "certificateSubject")} — válido até {When(_profile, "certificateNotAfter")[..10]}" : "";
        var missing = _profile.TryGetProperty("directProblems", out var dp) && dp.ValueKind == JsonValueKind.Array && dp.GetArrayLength() > 0
            ? "\nFalta para emitir: " + string.Join(", ", dp.EnumerateArray().Select(x => x.GetString())) : "";
        Summary = (configured
            ? $"Emitente CNPJ {Str(_profile, "cnpj")} • {Str(_profile, "uf")} • {(_profile.GetProperty("environment").GetInt32() == 1 ? "PRODUÇÃO" : "homologação (testes)")} • emissão {(_profile.GetProperty("autoEmit").GetBoolean() ? "automática" : "manual")} • série {Int(_profile, "nfceSeries")}"
            : "Emitente não configurado: as vendas ficam com NFC-e pendente até você terminar a configuração.") +
            "\nNota nas vendas: " + (Str(_profile, "emissionMode") switch { "a_pedido" => "só quando o cliente pedir", "nunca" => "não emite (só comprovante simples)", _ => "sempre" }) +
            (direct ? $"\nModo: direto na SEFAZ (sem custo por nota) • próximo número {Int(_profile, "nfceNextNumber")}" : Str(_profile, "provider") == "focus" ? "\nModo: Focus NFe" : "") + cert + missing;
    }

    /// <summary>Envia o certificado A1 (.pfx) da loja. A senha vai junto e fica criptografada no servidor.</summary>
    public RelayCommand CertificateCommand => RelayCommand.Sync(_ =>
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Certificado A1 (*.pfx;*.p12)|*.pfx;*.p12", Title = "Certificado digital A1 da loja" };
        if (dlg.ShowDialog() != true) return;
        var bytes = System.IO.File.ReadAllBytes(dlg.FileName);
        Shell.ShowPrompt(new PromptViewModel
        {
            Title = "Senha do certificado", Message = "Digite a senha do arquivo .pfx. Ela fica criptografada no servidor e não aparece em nenhuma tela.",
            PasswordLabel = "Senha do certificado", ConfirmText = "Enviar certificado",
        }, async p =>
        {
            try
            {
                _profile = await S.Api.UploadAsync("/api/v1/fiscal/certificate", bytes, System.IO.Path.GetFileName(dlg.FileName), "application/x-pkcs12",
                    new Dictionary<string, string> { ["password"] = p.Password });
                await LoadAsync();
                Info = "Certificado instalado.";
                return true;
            }
            catch (ApiException ex)
            {
                p.Error = ex.Code switch
                {
                    "certificate_invalid" => "Arquivo inválido ou senha errada.",
                    "certificate_expired" => "Este certificado está vencido.",
                    "certificate_cnpj_mismatch" => "O certificado é de outro CNPJ.",
                    "fiscal_profile_missing" => "Salve primeiro os dados do emitente (CNPJ).",
                    _ => "Falha: " + ex.Code,
                };
            }
            catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => CanConfigure);

    public RelayCommand VoidCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Inutilizar numeração", Message = "Use quando números da NFC-e foram pulados (ex.: falha). Informe a faixa, como 10-12. O XML da inutilização é guardado no arquivo fiscal.",
        ValueLabel = "Faixa (início-fim)", TextLabel = "Justificativa (15 a 255 caracteres)", ConfirmText = "Inutilizar",
    }, async p =>
    {
        var parts = p.Value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[^1], out var b)) { p.Error = "Informe a faixa como 10-12."; return false; }
        try
        {
            var series = _profile.ValueKind == JsonValueKind.Object && _profile.TryGetProperty("nfceSeries", out var s) ? s.GetInt32() : 1;
            var r = await S.Api.PostAsync("/api/v1/fiscal/void-numbers", new { series, firstNumber = a, lastNumber = b, reason = p.Text.Trim() });
            Info = $"Inutilização {r.GetProperty("status").GetString()}: {Str(r, "message")}";
            return true;
        }
        catch (ApiException ex) { p.Error = Friendly(ex.Code, ex.Message); }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => CanConfigure);

    public RelayCommand ConfigureCommand => RelayCommand.Sync(_ =>
    {
        var pr = _profile;
        string P(string n, string d = "") => pr.ValueKind == JsonValueKind.Object && pr.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : d;
        var f = new FormViewModel { Title = "Dados do emitente (NFC-e)", Message = "Use os dados do CNPJ da loja, como estão no cadastro da SEFAZ. Na emissão direta, envie também o certificado A1 (botão \"Certificado digital\").", ConfirmText = "Salvar" };
        f.Fields.Add(new FormField("emission", "Nota fiscal nas vendas", Emission[Math.Max(0, Array.IndexOf(EmissionKeys, P("emissionMode", "sempre")))], options: Emission)
            { Note = "Quem é obrigado a emitir NFC-e deve usar \"Sempre\". \"Só quando o cliente pedir\" é para quem é dispensado (ex.: MEI em vários estados) — confirme com o contador." });
        f.Fields.Add(new FormField("mode", "Como emitir", P("provider") == "focus" ? Modes[1] : Modes[0], options: Modes));
        f.Fields.Add(new FormField("cnpj", "CNPJ da loja", P("cnpj"), Icons.Edit));
        f.Fields.Add(new FormField("legal", "Razão social", P("legalName"), Icons.Edit));
        f.Fields.Add(new FormField("trade", "Nome fantasia", P("tradeName"), Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("ie", "Inscrição estadual", P("stateRegistration"), Icons.Edit, "opcional para MEI"));
        f.Fields.Add(new FormField("regime", "Regime tributário", Regimes[Math.Clamp(int.TryParse(P("regime", "1"), out var rg) ? rg - 1 : 0, 0, 3)], options: Regimes));
        f.Fields.Add(new FormField("street", "Rua / avenida", P("street"), Icons.Edit));
        f.Fields.Add(new FormField("number", "Número", P("addressNumber"), Icons.Edit));
        f.Fields.Add(new FormField("district", "Bairro", P("district"), Icons.Edit));
        f.Fields.Add(new FormField("city", "Município", P("cityName"), Icons.Edit));
        f.Fields.Add(new FormField("ibge", "Código IBGE do município", P("cityCode") == "0" ? "" : P("cityCode"), Icons.Edit, "7 dígitos (ex.: São Paulo 3550308)"));
        f.Fields.Add(new FormField("uf", "UF", P("uf", "SP"), Icons.Edit));
        f.Fields.Add(new FormField("cep", "CEP", P("cep"), Icons.Edit));
        f.Fields.Add(new FormField("phone", "Telefone", P("phone"), Icons.Edit, "opcional"));
        f.Fields.Add(new FormField("env", "Ambiente", P("environment", "2") == "1" ? Envs[1] : Envs[0], options: Envs));
        f.Fields.Add(new FormField("series", "Série da NFC-e", P("nfceSeries", "1"), Icons.Edit));
        f.Fields.Add(new FormField("next", "Próximo número da NFC-e", P("nfceNextNumber", "1"), Icons.Edit, "se já emitia em outro sistema, continue a numeração"));
        f.Fields.Add(new FormField("cscid", "ID do CSC (token)", P("cscId"), Icons.Edit, "gerado no site da SEFAZ"));
        f.Fields.Add(new FormField("csc", "CSC (código de segurança)", "", Icons.Edit, P("hasCsc") == "True" ? "já configurado — preencha só para trocar" : "gerado no site da SEFAZ"));
        f.Fields.Add(new FormField("auto", "Emitir automaticamente após cada venda", P("autoEmit", "True") == "False" ? "Não" : "Sim", options: ["Sim", "Não"]));
        f.Fields.Add(new FormField("token", "Token do provedor (só Focus NFe)", "", Icons.Edit, P("hasToken") == "True" ? "já configurado — preencha só para trocar" : "deixe vazio na emissão direta"));
        Shell.ShowForm(f, async form =>
        {
            try
            {
                _profile = await S.Api.PutAsync("/api/v1/fiscal/profile", new
                {
                    cnpj = form["cnpj"], stateRegistration = form["ie"], regime = Array.IndexOf(Regimes, form["regime"]) + 1, uf = form["uf"].ToUpperInvariant(),
                    environment = form["env"] == Envs[1] ? 1 : 2, provider = form["mode"] == Modes[1] ? "focus" : "sefaz", providerToken = form["token"].Length == 0 ? null : form["token"],
                    nfceSeries = int.TryParse(form["series"], out var sr) ? sr : 1, autoEmit = form["auto"] == "Sim",
                    legalName = form["legal"], tradeName = form["trade"], street = form["street"], addressNumber = form["number"], district = form["district"],
                    cityName = form["city"], cityCode = int.TryParse(form["ibge"], out var ib) ? ib : (int?)null, cep = form["cep"], phone = form["phone"],
                    cscId = form["cscid"], csc = form["csc"].Length == 0 ? null : form["csc"], nfceNextNumber = int.TryParse(form["next"], out var nx) ? nx : (int?)null,
                    emissionMode = EmissionKeys[Math.Max(0, Array.IndexOf(Emission, form["emission"]))],
                });
                await LoadAsync();
                Info = "Dados fiscais salvos.";
                return true;
            }
            catch (ApiException ex) { form.Error = ex.Code switch { "invalid_cnpj" => "CNPJ inválido.", "invalid_uf" => "UF inválida.", "invalid_city" => "Código IBGE: 7 dígitos.",
                "invalid_cep" => "CEP: 8 dígitos.", "invalid_next_number" => "O próximo número precisa ser maior que o último já usado.", _ => "Falha: " + ex.Code }; }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => CanConfigure);

    // ---------------- Pré-visualização (capturas sem servidor) ----------------

    public void ShowForPreview(string section, Action<FiscalViewModel> fill)
    {
        _section = Sections.First(s => s.Key == section);
        Raise(nameof(Section));
        foreach (var s in Sections) Raise("Is" + Cap(s.Key));
        fill(this);
    }
}
