using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

public sealed record FiscalDocRow(Guid Id, string When, string Number, string Status, int StatusCode, string Message, string? DanfeUrl, string Key)
{
    public bool CanTransmit => StatusCode is 1 or 3 or 4;
    public bool CanCancel => StatusCode == 2;
}

/// <summary>NFC-e da loja: dados do emitente, documentos emitidos, cancelamento e inutilização.</summary>
public sealed class FiscalViewModel(ShellViewModel shell) : OwnerPage(shell, "Fiscal", "NFC-e: emitente, notas emitidas, cancelamento e inutilização")
{
    public ObservableCollection<FiscalDocRow> Docs { get; } = [];
    private FiscalDocRow? _selected;
    private string _summary = "", _info = "";
    private JsonElement _profile;
    public FiscalDocRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;
    public bool CanConfigure => S.Session.Can("payments.configure");

    private static readonly string[] Regimes = ["Simples Nacional", "Simples Nacional - excesso de sublimite", "Regime normal", "MEI"];
    private static readonly string[] Envs = ["Homologação (testes, sem valor fiscal)", "Produção (valor fiscal)"];

    private static string StatusName(int s) => s switch { 1 => "Pendente", 2 => "Autorizada", 3 => "Rejeitada", 4 => "Contingência", 5 => "Cancelada", _ => s.ToString() };
    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private async Task Do(Func<Task> a)
    {
        Error = ""; Info = "";
        try { await a(); var m = Info; await LoadAsync(); Info = m; }
        catch (ApiException ex) { Error = ex.Code switch
        {
            "fiscal_cancel_window" => "Prazo de cancelamento (30 min) encerrado. Fale com o contador.",
            "fiscal_not_configured" => "Configure o emitente e o token do provedor fiscal.",
            "reason_required" => "Justificativa precisa ter de 15 a 255 caracteres.",
            _ => "Não foi possível: " + ex.Code,
        }; }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }

    public RelayCommand TransmitCommand => new(async _ => await Do(async () =>
    {
        var r = await S.Api.PostAsync($"/api/v1/fiscal/documents/{Selected!.Id}/transmit", new { });
        Info = $"{StatusName(r.GetProperty("status").GetInt32())}: {Str(r, "lastMessage")}";
    }), _ => Selected is { CanTransmit: true });

    public RelayCommand CancelCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Cancelar NFC-e " + Selected!.Number, Message = "Só é possível em até 30 minutos da autorização. A justificativa vai para a SEFAZ.",
        TextLabel = "Justificativa (15 a 255 caracteres)", ConfirmText = "Cancelar NFC-e",
    }, async p =>
    {
        try { await S.Api.PostAsync($"/api/v1/fiscal/documents/{Selected!.Id}/cancel", new { reason = p.Text.Trim() }); await LoadAsync(); Info = "NFC-e cancelada."; return true; }
        catch (ApiException ex) { p.Error = ex.Code == "reason_required" ? "Justificativa: de 15 a 255 caracteres." : ex.Code == "fiscal_cancel_window" ? "Prazo de 30 minutos encerrado." : "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => Selected is { CanCancel: true });

    public RelayCommand OpenDanfeCommand => RelayCommand.Sync(_ =>
    {
        if (Selected?.DanfeUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }, _ => Selected?.DanfeUrl is not null);

    public RelayCommand VoidCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Inutilizar numeração", Message = "Use quando números da NFC-e foram pulados (ex.: falha). Informe a faixa, como 10-12.",
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
        catch (ApiException ex) { p.Error = "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => CanConfigure);

    public RelayCommand ConfigureCommand => RelayCommand.Sync(_ =>
    {
        var pr = _profile;
        string P(string n, string d = "") => pr.ValueKind == JsonValueKind.Object && pr.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : d;
        var f = new FormViewModel { Title = "Dados do emitente (NFC-e)", Message = "Use os dados do CNPJ da loja. O certificado digital A1 é enviado no painel do provedor fiscal (Focus NFe).", ConfirmText = "Salvar" };
        f.Fields.Add(new FormField("cnpj", "CNPJ da loja", P("cnpj"), Icons.Edit));
        f.Fields.Add(new FormField("ie", "Inscrição estadual", P("stateRegistration"), Icons.Edit, "opcional para MEI"));
        f.Fields.Add(new FormField("regime", "Regime tributário", Regimes[Math.Clamp(int.TryParse(P("regime", "1"), out var rg) ? rg - 1 : 0, 0, 3)], options: Regimes));
        f.Fields.Add(new FormField("uf", "UF", P("uf", "SP"), Icons.Edit));
        f.Fields.Add(new FormField("env", "Ambiente", P("environment", "2") == "1" ? Envs[1] : Envs[0], options: Envs));
        f.Fields.Add(new FormField("series", "Série da NFC-e", P("nfceSeries", "1"), Icons.Edit));
        f.Fields.Add(new FormField("auto", "Emitir automaticamente após cada venda", P("autoEmit", "True") == "False" ? "Não" : "Sim", options: ["Sim", "Não"]));
        f.Fields.Add(new FormField("token", "Token do provedor (Focus NFe)", "", Icons.Edit, P("hasToken") == "True" ? "já configurado — preencha só para trocar" : "cole o token"));
        Shell.ShowForm(f, async form =>
        {
            try
            {
                _profile = await S.Api.PutAsync("/api/v1/fiscal/profile", new
                {
                    cnpj = form["cnpj"], stateRegistration = form["ie"], regime = Array.IndexOf(Regimes, form["regime"]) + 1, uf = form["uf"].ToUpperInvariant(),
                    environment = form["env"] == Envs[1] ? 1 : 2, provider = "focus", providerToken = form["token"].Length == 0 ? null : form["token"],
                    nfceSeries = int.TryParse(form["series"], out var sr) ? sr : 1, autoEmit = form["auto"] == "Sim",
                });
                await LoadAsync();
                Info = "Dados fiscais salvos.";
                return true;
            }
            catch (ApiException ex) { form.Error = ex.Code switch { "invalid_cnpj" => "CNPJ inválido.", "invalid_uf" => "UF inválida.", _ => "Falha: " + ex.Code }; }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => CanConfigure);

    protected override async Task LoadCoreAsync()
    {
        _profile = await S.Api.GetAsync("/api/v1/fiscal/profile");
        var configured = _profile.TryGetProperty("configured", out var c) && c.GetBoolean();
        Summary = configured
            ? $"Emitente CNPJ {Str(_profile, "cnpj")} • {Str(_profile, "uf")} • {(_profile.GetProperty("environment").GetInt32() == 1 ? "PRODUÇÃO" : "homologação (testes)")} • emissão {(_profile.GetProperty("autoEmit").GetBoolean() ? "automática" : "manual")}"
            : "Emitente não configurado: as vendas ficam com NFC-e pendente até você configurar o CNPJ e o provedor fiscal.";
        Docs.Clear();
        foreach (var d in (await S.Api.GetAsync("/api/v1/fiscal/documents")).EnumerateArray().Take(200))
        {
            var st = d.GetProperty("status").GetInt32();
            var num = d.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? $"{n.GetInt32()}" : "—";
            var key = Str(d, "accessKey") ?? "";
            Docs.Add(new FiscalDocRow(d.GetProperty("id").GetGuid(), d.GetProperty("createdAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM/yyyy HH:mm"), num,
                StatusName(st), st, Str(d, "lastMessage") ?? "", Str(d, "danfeUrl"), key.Length > 12 ? "…" + key[^12..] : key));
        }
    }
}
