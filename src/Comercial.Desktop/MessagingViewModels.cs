using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

/// <summary>Um ícone do menu lateral na engrenagem: ordem e se aparece.</summary>
public sealed class NavPref(NavItem item, bool visible, Action changed) : ObservableObject
{
    private bool _visible = visible;
    public NavItem Item { get; } = item;
    public string Title => Item.Title;
    public string Glyph => Item.Glyph;
    /// <summary>"Vendas" é a tela do caixa: nunca some.</summary>
    public bool CanHide => Item.Key != "pos";
    public bool Visible { get => _visible; set { if (Set(ref _visible, value)) changed(); } }
}

public sealed record WaMessageRow(string When, string Kind, string Customer, string Status, string Detail, bool Failed);

/// <summary>
/// Engrenagem do cabeçalho: menu lateral deste computador (ordem e ícones escondidos) e o WhatsApp da loja
/// (API oficial da Meta) para a cobrança do fiado e as promoções.
/// </summary>
public sealed class SettingsViewModel : OwnerPage
{
    private string _waStatus = "", _info = "", _webhookUrl = "", _verifyToken = "", _chargeTemplate = "cobranca_fiado", _promoTemplate = "promocao_loja",
        _language = "pt_BR", _chargeText = "", _promoText = "";
    private bool _connected, _charges, _promos, _hasSecret;
    private int _dailyLimit = 200, _interval = 7;

    public SettingsViewModel(ShellViewModel shell) : base(shell, "Configurações", "Menu lateral deste computador e WhatsApp da loja")
    {
        BuildMenu();
    }

    // ---------------- Menu lateral ----------------
    public ObservableCollection<NavPref> Menu { get; } = [];

    private void BuildMenu()
    {
        Menu.Clear();
        var order = S.Settings.NavOrder;
        foreach (var n in Shell.AvailableNav.OrderBy(n => order.IndexOf(n.Key) is var i && i >= 0 ? i : order.Count + Shell.AvailableNav.IndexOf(n)))
            Menu.Add(new NavPref(n, !S.Settings.NavHidden.Contains(n.Key), SaveMenu));
    }

    private void SaveMenu()
    {
        S.Settings.NavOrder = Menu.Select(m => m.Item.Key).ToList();
        S.Settings.NavHidden = Menu.Where(m => !m.Visible && m.CanHide).Select(m => m.Item.Key).ToList();
        S.Settings.Save();
        Shell.ApplyNavPrefs();
    }

    public RelayCommand MoveUpCommand => RelayCommand.Sync(p => Move(p as NavPref, -1));
    public RelayCommand MoveDownCommand => RelayCommand.Sync(p => Move(p as NavPref, 1));
    private void Move(NavPref? p, int delta)
    {
        if (p is null) return;
        var i = Menu.IndexOf(p);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Menu.Count) return;
        Menu.Move(i, j);
        SaveMenu();
    }
    public RelayCommand ResetMenuCommand => RelayCommand.Sync(_ =>
    {
        S.Settings.NavOrder = []; S.Settings.NavHidden = [];
        S.Settings.Save();
        Shell.ApplyNavPrefs();
        BuildMenu();
    });

    // ---------------- WhatsApp da loja ----------------
    private bool _preview;
    public bool CanWhatsApp => _preview || S.Session.Can("messaging.manage", "crm");
    public bool Connected { get => _connected; set => Set(ref _connected, value); }
    public string WaStatus { get => _waStatus; set => Set(ref _waStatus, value); }
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;
    public string WebhookUrl { get => _webhookUrl; set => Set(ref _webhookUrl, value); }
    public string VerifyToken { get => _verifyToken; set => Set(ref _verifyToken, value); }
    public string ChargeTemplateText { get => _chargeText; set => Set(ref _chargeText, value); }
    public string PromoTemplateText { get => _promoText; set => Set(ref _promoText, value); }
    public string OptionsText => $"Cobrança automática do fiado: {(_charges ? "LIGADA" : "desligada")}  •  Promoções: {(_promos ? "LIGADAS" : "desligadas")}\n"
        + $"Limite: até {_dailyLimit} promoções por dia, cada cliente no máximo 1 a cada {_interval} dias, só das 9h às 20h.\n"
        + $"Modelos na Meta: cobrança \"{_chargeTemplate}\", promoção \"{_promoTemplate}\" ({_language})."
        + (_hasSecret ? "" : "\nPara quem responder SAIR deixar de receber ofertas, informe a chave secreta do app (botão Conectar).");
    public ObservableCollection<WaMessageRow> Messages { get; } = [];

    protected override async Task LoadCoreAsync()
    {
        if (!CanWhatsApp) return;
        var s = await S.Api.GetAsync("/api/v1/whatsapp/settings");
        Apply(s);
        Messages.Clear();
        foreach (var m in (await S.Api.GetAsync("/api/v1/whatsapp/messages")).EnumerateArray())
        {
            var failed = m.GetProperty("status").GetString() != "sent";
            var kind = m.GetProperty("kind").GetString() switch { "charge" => "Cobrança", "promo" => "Promoção", _ => "Teste" };
            Messages.Add(new WaMessageRow(m.GetProperty("createdAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM HH:mm"), kind,
                Str(m, "customer") is { Length: > 0 } c ? c : Str(m, "phone"), failed ? "Falhou" : "Enviada", failed ? Str(m, "error") : Str(m, "preview"), failed));
        }
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private void Apply(JsonElement s)
    {
        Connected = s.GetProperty("connected").GetBoolean();
        WaStatus = Connected ? $"Conectado: {Str(s, "verifiedName")} {Str(s, "displayPhone")} • token {Str(s, "tokenHint")}" : "WhatsApp da loja não conectado.";
        _charges = s.GetProperty("chargesEnabled").GetBoolean();
        _promos = s.GetProperty("promotionsEnabled").GetBoolean();
        _dailyLimit = s.GetProperty("dailyPromoLimit").GetInt32();
        _interval = s.GetProperty("promoIntervalDays").GetInt32();
        _chargeTemplate = Str(s, "chargeTemplate"); _promoTemplate = Str(s, "promoTemplate"); _language = Str(s, "language");
        _hasSecret = s.GetProperty("hasAppSecret").GetBoolean();
        WebhookUrl = S.Settings.ApiBaseUrl.TrimEnd('/') + Str(s, "webhookPath");
        VerifyToken = Str(s, "webhookVerifyToken");
        ChargeTemplateText = Str(s, "chargeTemplateText");
        PromoTemplateText = Str(s, "promoTemplateText");
        Raise(nameof(OptionsText));
    }

    private object Body(string? phoneNumberId = null, string? token = null, string? secret = null, bool disconnect = false) => new
    {
        phoneNumberId, accessToken = token, appSecret = secret, disconnect, chargeTemplate = _chargeTemplate, promoTemplate = _promoTemplate, language = _language,
        chargesEnabled = !disconnect && _charges, promotionsEnabled = !disconnect && _promos, dailyPromoLimit = _dailyLimit, promoIntervalDays = _interval,
    };

    private static string ErrorText(ApiException ex) => ex.Code switch
    {
        "whatsapp_rejected" or "invalid_phone_number_id" or "invalid_token" or "invalid_app_secret" or "invalid_template" or "invalid_limit" or "invalid_interval"
            or "whatsapp_not_connected" or "whatsapp_send_failed" or "invalid_phone" => ex.Message,
        _ => "Falha: " + ex.Code,
    };

    public RelayCommand ConnectCommand => RelayCommand.Sync(_ =>
    {
        var f = new FormViewModel
        {
            Title = "Conectar o WhatsApp da loja (API oficial)",
            Message = "No painel da Meta (developers.facebook.com > seu app > WhatsApp > Configuração da API) copie a Identificação do número de telefone e um token de acesso permanente (usuário do sistema). O token fica guardado criptografado.",
            ConfirmText = "Conectar",
        };
        f.Fields.Add(new FormField("id", "Identificação do número de telefone", "", Icons.Edit, "só números"));
        f.Fields.Add(new FormField("token", "Token de acesso", "", Icons.Edit, Connected ? "vazio = manter o atual" : "EAAG..."));
        f.Fields.Add(new FormField("secret", "Chave secreta do app (opcional, para o SAIR)", "", Icons.Edit, "Configurações do app > Básico"));
        Shell.ShowForm(f, async form =>
        {
            try
            {
                var s = await S.Api.PutAsync("/api/v1/whatsapp/settings", Body(form["id"].Length == 0 ? null : form["id"], form["token"].Length == 0 ? null : form["token"], form["secret"].Length == 0 ? null : form["secret"]));
                Apply(s);
                Info = "WhatsApp conectado. Agora ligue a cobrança e/ou as promoções em Opções, e faça um teste.";
                return true;
            }
            catch (ApiException ex) { form.Error = ErrorText(ex); }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => CanWhatsApp);

    public RelayCommand OptionsCommand => RelayCommand.Sync(_ =>
    {
        string[] yesNo = ["Sim", "Não"];
        var f = new FormViewModel { Title = "Opções do WhatsApp", Message = "Os limites protegem o número da loja: a Meta bloqueia quem manda mensagens demais ou recebe muitas denúncias.", ConfirmText = "Salvar" };
        f.Fields.Add(new FormField("charges", "Cobrar o fiado automaticamente no dia escolhido por cada cliente?", _charges ? "Sim" : "Não", options: yesNo));
        f.Fields.Add(new FormField("promos", "Enviar promoções (só para quem aceitou receber ofertas)?", _promos ? "Sim" : "Não", options: yesNo));
        f.Fields.Add(new FormField("limit", "Máximo de promoções por dia", _dailyLimit.ToString(), Icons.Edit, "comece com 200 (número novo na Meta)"));
        f.Fields.Add(new FormField("interval", "Cada cliente recebe no máximo 1 promoção a cada (dias)", _interval.ToString(), Icons.Edit, "mínimo 3"));
        f.Fields.Add(new FormField("charge", "Nome do modelo de cobrança na Meta", _chargeTemplate, Icons.Edit));
        f.Fields.Add(new FormField("promo", "Nome do modelo de promoção na Meta", _promoTemplate, Icons.Edit));
        Shell.ShowForm(f, async form =>
        {
            if (!int.TryParse(form["limit"], out var limit) || !int.TryParse(form["interval"], out var interval)) { form.Error = "Use só números no limite e no intervalo."; return false; }
            var (c0, p0, l0, i0, ct0, pt0) = (_charges, _promos, _dailyLimit, _interval, _chargeTemplate, _promoTemplate);
            (_charges, _promos, _dailyLimit, _interval, _chargeTemplate, _promoTemplate) = (form["charges"] == "Sim", form["promos"] == "Sim", limit, interval, form["charge"], form["promo"]);
            try { Apply(await S.Api.PutAsync("/api/v1/whatsapp/settings", Body())); Info = "Opções salvas."; return true; }
            catch (ApiException ex) { form.Error = ErrorText(ex); }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            (_charges, _promos, _dailyLimit, _interval, _chargeTemplate, _promoTemplate) = (c0, p0, l0, i0, ct0, pt0);
            return false;
        });
    }, _ => CanWhatsApp);

    public RelayCommand TestCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Testar o WhatsApp", Message = "Envia a mensagem de teste da Meta (hello_world) para o celular informado.", ValueLabel = "Celular com DDD", ConfirmText = "Enviar teste",
    }, async p =>
    {
        try { await S.Api.PostAsync("/api/v1/whatsapp/test", new { phone = p.Value }); Info = "Teste enviado. Confira no celular."; await LoadAsync(); return true; }
        catch (ApiException ex) { p.Error = ErrorText(ex); }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => CanWhatsApp);

    public RelayCommand DisconnectCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Desconectar o WhatsApp", Message = "Para a cobrança automática e as promoções e apaga o token guardado. Você pode conectar de novo quando quiser.", ConfirmText = "Desconectar",
    }, async p =>
    {
        try { Apply(await S.Api.PutAsync("/api/v1/whatsapp/settings", Body(disconnect: true))); Info = "WhatsApp desconectado."; return true; }
        catch (ApiException ex) { p.Error = ErrorText(ex); }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => CanWhatsApp);

    public RelayCommand CopyWebhookCommand => RelayCommand.Sync(_ =>
    {
        try { System.Windows.Clipboard.SetText($"URL de retorno: {WebhookUrl}\nToken de verificação: {VerifyToken}"); Info = "Endereço e token copiados."; }
        catch (System.Runtime.InteropServices.COMException) { Info = "Não foi possível copiar agora. Tente de novo."; }
    });

    public RelayCommand CopyTemplatesCommand => RelayCommand.Sync(_ =>
    {
        try
        {
            System.Windows.Clipboard.SetText($"Modelo de cobrança (categoria Utilidade, nome {_chargeTemplate}, idioma Português BR):\n{ChargeTemplateText}\n\n"
                + $"Modelo de promoção (categoria Marketing, nome {_promoTemplate}, idioma Português BR):\n{PromoTemplateText}");
            Info = "Textos dos modelos copiados. Cole no Gerenciador do WhatsApp (Modelos de mensagem) e envie para aprovação da Meta.";
        }
        catch (System.Runtime.InteropServices.COMException) { Info = "Não foi possível copiar agora. Tente de novo."; }
    });

    /// <summary>Usado só pelas capturas de tela.</summary>
    public void SetForPreview()
    {
        _preview = true;
        Raise(nameof(CanWhatsApp));
        Apply(JsonDocument.Parse("""
            {"connected":true,"phoneNumberId":"123456789012345","displayPhone":"+55 47 3333-4444","verifiedName":"Loja Demo","tokenHint":"final ...a1b2","hasAppSecret":true,
             "webhookPath":"/webhooks/whatsapp/7d28c068-31f8-476a-b2fb-98003de4d804","webhookVerifyToken":"9f2c4e7a1b3d5f60","chargeTemplate":"cobranca_fiado","promoTemplate":"promocao_loja",
             "language":"pt_BR","chargesEnabled":true,"promotionsEnabled":true,"dailyPromoLimit":200,"promoIntervalDays":7,
             "chargeTemplateText":"Olá, {{1}}! Aqui é da {{2}}. Seu saldo no crediário é de {{3}}. Compras em aberto: {{4}}. Para pagar ou tirar dúvidas, responda esta mensagem.",
             "promoTemplateText":"Olá, {{1}}! Novidade da {{2}}: {{3}}"}
            """).RootElement);
        Messages.Add(new WaMessageRow("31/10 10:00", "Cobrança", "Maria Silva", "Enviada", "Olá, Maria! Aqui é da Loja Demo. Seu saldo no crediário é de R$ 49,80. Compras em aberto: 28/10: Caneca x2 (R$ 49,80)...", false));
        Messages.Add(new WaMessageRow("30/10 15:12", "Promoção", "João Souza", "Enviada", "Olá, João! Novidade da Loja Demo: Tudo com 10% neste sábado!", false));
        Messages.Add(new WaMessageRow("30/10 15:12", "Promoção", "•••7777", "Falhou", "Número sem WhatsApp ou não pode receber mensagens.", true));
    }
}

public sealed record CampaignRow(Guid Id, string Title, string When, string Status, string Progress, string Message, bool CanCancel);

/// <summary>Promoções em massa pelo WhatsApp da loja, com os limites anti-spam do servidor à vista.</summary>
public sealed class CampaignsViewModel(ShellViewModel shell) : OwnerPage(shell, "Campanhas", "Promoções pelo WhatsApp da loja, só para quem aceitou receber ofertas")
{
    private string _limits = "", _info = "";
    private CampaignRow? _selected;
    private bool _ready;
    public ObservableCollection<CampaignRow> Campaigns { get; } = [];
    public CampaignRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public string LimitsText { get => _limits; set => Set(ref _limits, value); }
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;

    protected override async Task LoadCoreAsync()
    {
        var r = await S.Api.GetAsync("/api/v1/whatsapp/campaigns");
        Fill(r);
    }

    private void Fill(JsonElement r)
    {
        _ready = r.GetProperty("connected").GetBoolean() && r.GetProperty("promotionsEnabled").GetBoolean();
        Info = _ready ? "" : "Para enviar promoções, conecte o WhatsApp da loja e ligue as promoções na engrenagem (canto de cima) > WhatsApp > Opções.";
        LimitsText = $"Hoje: {r.GetProperty("sentToday").GetInt32()} de {r.GetProperty("dailyLimit").GetInt32()} promoções  •  "
            + $"{r.GetProperty("eligibleNow").GetInt32()} cliente(s) podem receber agora (de {r.GetProperty("consenting").GetInt32()} que aceitaram ofertas)\n"
            + $"Cada cliente recebe no máximo 1 promoção a cada {r.GetProperty("intervalDays").GetInt32()} dias  •  envios só das {r.GetProperty("sendFromHour").GetInt32()}h às {r.GetProperty("sendUntilHour").GetInt32()}h  •  "
            + "1 campanha por dia e até 3 por semana  •  quem responder SAIR não recebe mais";
        Campaigns.Clear();
        foreach (var c in r.GetProperty("campaigns").EnumerateArray())
        {
            var status = c.GetProperty("status").GetString();
            var reason = c.TryGetProperty("stopReason", out var sr) && sr.ValueKind == JsonValueKind.String ? " • " + sr.GetString() : "";
            Campaigns.Add(new CampaignRow(c.GetProperty("id").GetGuid(), c.GetProperty("title").GetString()!,
                c.GetProperty("scheduledAt").GetDateTimeOffset().LocalDateTime.ToString("dd/MM/yyyy HH:mm"),
                status switch { "scheduled" => "Agendada", "sending" => "Enviando", "done" => "Concluída", "cancelled" => "Cancelada", _ => "Parada" },
                $"{c.GetProperty("sent").GetInt32()} enviada(s) • {c.GetProperty("failed").GetInt32()} falha(s){reason}",
                c.GetProperty("message").GetString()!, status is "scheduled" or "sending"));
        }
    }

    public RelayCommand NewCommand => RelayCommand.Sync(_ =>
    {
        var start = DateTime.Now.AddMinutes(10);
        var f = new FormViewModel
        {
            Title = "Nova campanha de promoção",
            Message = "A mensagem entra no modelo aprovado: \"Olá, [nome]! Novidade da [loja]: [sua mensagem]\". Seja direto e use no máximo um link.",
            ConfirmText = "Agendar",
        };
        f.Fields.Add(new FormField("title", "Nome da campanha (só para você)", "", Icons.Edit, "ex.: Promoção de sábado"));
        f.Fields.Add(new FormField("message", "Mensagem", "", Icons.Edit, "ex.: Tudo com 10% de desconto neste sábado!"));
        f.Fields.Add(new FormField("date", "Dia", start.ToString("dd/MM/yyyy"), Icons.Edit));
        f.Fields.Add(new FormField("time", "Hora", start.ToString("HH:mm"), Icons.Edit, "entre 9h e 20h"));
        Shell.ShowForm(f, async form =>
        {
            if (!DateTime.TryParseExact(form["date"] + " " + form["time"], "dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when))
            { form.Error = "Data no formato 31/12/2026 e hora 14:30."; return false; }
            try
            {
                await S.Api.PostAsync("/api/v1/whatsapp/campaigns", new { title = form["title"], message = form["message"], scheduledAt = new DateTimeOffset(when) });
                await LoadAsync();
                Info = "Campanha agendada. O envio é feito aos poucos, dentro dos limites.";
                return true;
            }
            catch (ApiException ex) { form.Error = ex.Code is "not_found" or "forbidden" ? "Falha: " + ex.Code : ex.Message; }
            catch (HttpRequestException) { form.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => _ready);

    public RelayCommand CancelCommand => RelayCommand.Sync(_ => Shell.ShowPrompt(new PromptViewModel
    {
        Title = "Cancelar campanha", Message = $"Para o envio de \"{Selected!.Title}\". Quem já recebeu não é afetado.", ConfirmText = "Cancelar campanha",
    }, async p =>
    {
        try { await S.Api.PostAsync($"/api/v1/whatsapp/campaigns/{Selected!.Id}/cancel", new { }); await LoadAsync(); return true; }
        catch (ApiException ex) { p.Error = "Falha: " + ex.Code; }
        catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
        return false;
    }), _ => Selected is { CanCancel: true });

    /// <summary>Usado só pelas capturas de tela.</summary>
    public void SetForPreview() => Fill(JsonDocument.Parse("""
        {"connected":true,"promotionsEnabled":true,"dailyLimit":200,"intervalDays":7,"sentToday":120,"eligibleNow":64,"consenting":310,"sendFromHour":9,"sendUntilHour":20,
         "campaigns":[
          {"id":"5b1e7c1a-0000-4000-8000-000000000001","title":"Promoção de sábado","message":"Tudo com 10% de desconto neste sábado!","scheduledAt":"2026-10-31T13:00:00Z","status":"sending","sent":120,"failed":2,"stopReason":null},
          {"id":"5b1e7c1a-0000-4000-8000-000000000002","title":"Dia das crianças","message":"Brinquedos com até 30% off até domingo.","scheduledAt":"2026-10-10T14:00:00Z","status":"done","sent":288,"failed":4,"stopReason":null},
          {"id":"5b1e7c1a-0000-4000-8000-000000000003","title":"Black Friday","message":"Black Friday chegando: prepare a lista!","scheduledAt":"2026-11-27T12:00:00Z","status":"scheduled","sent":0,"failed":0,"stopReason":null}]}
        """).RootElement);
}
