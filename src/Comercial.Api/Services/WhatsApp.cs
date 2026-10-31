using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Comercial.Api.Security;
using Comercial.Domain;
using Comercial.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

public sealed record WhatsAppSettingsRequest(string? PhoneNumberId, string? AccessToken, string? AppSecret, bool Disconnect,
    string? ChargeTemplate, string? PromoTemplate, string? Language, bool ChargesEnabled, bool PromotionsEnabled, int DailyPromoLimit, int PromoIntervalDays);
public sealed record WhatsAppSettingsView(bool Connected, string? PhoneNumberId, string? DisplayPhone, string? VerifiedName, string? TokenHint, bool HasAppSecret,
    string WebhookPath, string WebhookVerifyToken, string ChargeTemplate, string PromoTemplate, string Language, bool ChargesEnabled, bool PromotionsEnabled,
    int DailyPromoLimit, int PromoIntervalDays, string ChargeTemplateText, string PromoTemplateText);
public sealed record WhatsAppTestRequest(string Phone);
public sealed record BillingDayRequest(int? Day);
public sealed record ChargePreview(bool CanSend, string? Reason, string? Phone, long OpenCents, string Text, int? BillingDay, DateOnly? LastChargeSentOn);
public sealed record CampaignRequest(string Title, string Message, DateTimeOffset ScheduledAt, bool Manual = false);
public sealed record ManualSentRequest(Guid CustomerId);

public sealed record WaCheck(bool Ok, string Message, string? DisplayPhone, string? VerifiedName);
public sealed record WaSendResult(bool Ok, string? MessageId, string? Error);

/// <summary>WhatsApp Cloud API (graph.facebook.com). Mensagens iniciadas pela loja só com modelos aprovados.</summary>
public interface IWhatsAppClient
{
    Task<WaCheck> CheckAsync(string phoneNumberId, string token, CancellationToken ct);
    Task<WaSendResult> SendTemplateAsync(string phoneNumberId, string token, string to, string template, string language, IReadOnlyList<string> bodyParams, CancellationToken ct);
}

public sealed class WhatsAppCloudClient(HttpClient http, ILogger<WhatsAppCloudClient> log) : IWhatsAppClient
{
    public async Task<WaCheck> CheckAsync(string phoneNumberId, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{phoneNumberId}?fields=display_phone_number,verified_name,quality_rating");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return new(false, Error(text, (int)res.StatusCode), null, null);
            var n = JsonNode.Parse(text);
            var phone = n?["display_phone_number"]?.GetValue<string>();
            var name = n?["verified_name"]?.GetValue<string>();
            var quality = n?["quality_rating"]?.GetValue<string>();
            return new(true, $"Conectado: {name} ({phone})" + (quality is null ? "" : $" • qualidade {quality}"), phone, name);
        }
        catch (HttpRequestException) { return new(false, "Sem conexão com a Meta.", null, null); }
    }

    public async Task<WaSendResult> SendTemplateAsync(string phoneNumberId, string token, string to, string template, string language, IReadOnlyList<string> bodyParams, CancellationToken ct)
    {
        var components = new JsonArray();
        if (bodyParams.Count > 0)
        {
            var ps = new JsonArray();
            foreach (var p in bodyParams) ps.Add(new JsonObject { ["type"] = "text", ["text"] = p });
            components.Add(new JsonObject { ["type"] = "body", ["parameters"] = ps });
        }
        var body = new JsonObject
        {
            ["messaging_product"] = "whatsapp", ["to"] = to, ["type"] = "template",
            ["template"] = new JsonObject { ["name"] = template, ["language"] = new JsonObject { ["code"] = language }, ["components"] = components },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{phoneNumberId}/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("WhatsApp envio -> {Status}: {Body}", (int)res.StatusCode, text.Length > 400 ? text[..400] : text);
                return new(false, null, Error(text, (int)res.StatusCode));
            }
            return new(true, JsonNode.Parse(text)?["messages"]?[0]?["id"]?.GetValue<string>(), null);
        }
        catch (HttpRequestException) { return new(false, null, "Sem conexão com a Meta."); }
    }

    private static string Error(string text, int status)
    {
        try
        {
            var e = JsonNode.Parse(text)?["error"];
            var code = e?["code"]?.ToString();
            var msg = e?["message"]?.GetValue<string>() ?? "";
            var hint = code switch
            {
                "190" => "Token da Meta inválido ou vencido.",
                "132001" => "Modelo de mensagem não encontrado ou ainda não aprovado pela Meta.",
                "132000" => "Quantidade de variáveis diferente do modelo aprovado.",
                "131026" => "Número sem WhatsApp ou não pode receber mensagens.",
                "131047" => "Fora da janela de 24 horas: use um modelo aprovado.",
                "131056" => "Muitas mensagens para o mesmo número em pouco tempo.",
                "130429" or "131048" => "Limite de envio da Meta atingido. Tente mais tarde.",
                _ => null,
            };
            return (hint ?? "A Meta recusou o envio.") + (code is null ? "" : $" (código {code}: {msg})");
        }
        catch (System.Text.Json.JsonException) { return $"A Meta recusou o envio (HTTP {status})."; }
    }
}

/// <summary>
/// WhatsApp da loja: cobrança do fiado (manual ou no dia escolhido pelo cliente) e promoções em massa com limites
/// anti-spam: só para quem aceitou ofertas, no máximo uma promoção por cliente a cada N dias, teto diário, envio
/// espaçado, só em horário comercial, parada automática com muitas falhas e descadastro respondendo SAIR.
/// </summary>
public sealed class WhatsAppService(AppDbContext db, RequestActor actor, AuditService audit, IDataProtectionProvider dp, IWhatsAppClient wa,
    CreditService credit, ScopedLookup lookup, TimeProvider clock, IConfiguration config)
{
    public const int SendFromHour = 9, SendUntilHour = 20;
    public const int MaxPromoLimit = 1000, MinIntervalDays = 3;
    public static readonly string[] OptOutWords = ["SAIR", "PARAR", "STOP", "CANCELAR", "DESCADASTRAR"];
    private IDataProtector Protector => dp.CreateProtector("Comercial.WhatsApp.v1");
    private static readonly CultureInfo Br = new("pt-BR");

    public static string ChargeTemplateText =>
        "Olá, {{1}}! Aqui é da {{2}}. Seu saldo no crediário é de {{3}}. Compras em aberto: {{4}}. Para pagar ou tirar dúvidas, responda esta mensagem.";
    public static string PromoTemplateText =>
        "Olá, {{1}}! Novidade da {{2}}: {{3}}  (rodapé do modelo: Para não receber mais ofertas, responda SAIR)";

    public static DateTimeOffset Local(DateTimeOffset utc) => utc.ToOffset(TimeSpan.FromHours(-3));
    private DateTimeOffset Now => clock.GetUtcNow();
    private static string Money(long cents) => (cents / 100m).ToString("C", Br);

    /// <summary>Celular brasileiro em formato internacional (55 + DDD + número). Inválido: null.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var d = new string(phone.Where(char.IsDigit).ToArray());
        if (d.StartsWith("0")) d = d.TrimStart('0');
        if (d.Length is 10 or 11) d = "55" + d;
        return d.Length is 12 or 13 && d.StartsWith("55") ? d : null;
    }

    /// <summary>Variável de modelo: a Meta não aceita quebra de linha, tabulação nem mais de 4 espaços seguidos.</summary>
    public static string Param(string text, int max)
    {
        var t = Regex.Replace(text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), " {2,}", " ").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    private Task<TenantWhatsAppSettings?> FindAsync() => db.WhatsAppSettings.FirstOrDefaultAsync(x => x.TenantId == actor.TenantId);

    // ---------------- Configuração ----------------

    public async Task<WhatsAppSettingsView> GetSettingsAsync()
    {
        var s = await FindAsync() ?? new TenantWhatsAppSettings();
        string? hint = null;
        if (s.AccessTokenProtected is { } p) { var tok = Protector.Unprotect(p); hint = "final ..." + tok[^4..]; }
        return new(s.AccessTokenProtected is not null && s.PhoneNumberId is not null, s.PhoneNumberId, s.DisplayPhone, s.VerifiedName, hint, s.AppSecretProtected is not null,
            $"/webhooks/whatsapp/{actor.TenantId}", s.WebhookVerifyToken, s.ChargeTemplate, s.PromoTemplate, s.Language, s.ChargesEnabled, s.PromotionsEnabled,
            s.DailyPromoLimit, s.PromoIntervalDays, ChargeTemplateText, PromoTemplateText);
    }

    public async Task<WhatsAppSettingsView> SaveSettingsAsync(WhatsAppSettingsRequest r, CancellationToken ct)
    {
        var s = await FindAsync();
        if (s is null) { s = new TenantWhatsAppSettings { TenantId = actor.TenantId }; db.WhatsAppSettings.Add(s); }
        if (s.WebhookVerifyToken.Length == 0) s.WebhookVerifyToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var credentialsChanged = false;
        if (r.Disconnect)
        {
            s.PhoneNumberId = null; s.AccessTokenProtected = null; s.AppSecretProtected = null; s.DisplayPhone = null; s.VerifiedName = null;
            credentialsChanged = true;
        }
        else
        {
            var phoneId = r.PhoneNumberId?.Trim();
            var token = r.AccessToken?.Trim();
            if (!string.IsNullOrEmpty(phoneId) && !Regex.IsMatch(phoneId, @"^\d{5,30}$"))
                throw new DomainException("invalid_phone_number_id", "A identificação do número tem só números (copie do painel da Meta: WhatsApp > Configuração da API).");
            // Erro comum: colar o próprio celular (10 a 13 dígitos). A identificação da Meta é um código maior, com 15 ou mais.
            if (!string.IsNullOrEmpty(phoneId) && phoneId.Length < 14)
                throw new DomainException("phone_instead_of_id", "Isso parece o número do celular. Use a \"Identificação do número de telefone\" (código com 15 ou mais dígitos) que aparece no painel da Meta, em WhatsApp > Configuração da API.");
            if (!string.IsNullOrEmpty(token) && (token.Length is < 20 or > 1000 || token.Any(char.IsWhiteSpace)))
                throw new DomainException("invalid_token", "Token de acesso da Meta inválido.");
            if (!string.IsNullOrEmpty(phoneId) || !string.IsNullOrEmpty(token))
            {
                var useId = phoneId ?? s.PhoneNumberId;
                var useToken = token ?? (s.AccessTokenProtected is { } p ? Protector.Unprotect(p) : null);
                if (useId is null || useToken is null) throw new DomainException("credentials_required", "Informe a identificação do número e o token de acesso.");
                var check = await wa.CheckAsync(useId, useToken, ct);
                if (!check.Ok) throw new DomainException("whatsapp_rejected", "A Meta recusou a conexão: " + check.Message);
                s.PhoneNumberId = useId; s.AccessTokenProtected = Protector.Protect(useToken);
                s.DisplayPhone = check.DisplayPhone; s.VerifiedName = check.VerifiedName;
                credentialsChanged = true;
            }
            if (!string.IsNullOrWhiteSpace(r.AppSecret))
            {
                var secret = r.AppSecret.Trim();
                if (!Regex.IsMatch(secret, "^[A-Fa-f0-9]{32}$")) throw new DomainException("invalid_app_secret", "A chave secreta do app tem 32 letras e números (painel da Meta: Configurações do app > Básico).");
                s.AppSecretProtected = Protector.Protect(secret);
                credentialsChanged = true;
            }
        }
        static string Template(string? v, string fallback, string field)
        {
            var t = (v ?? fallback).Trim().ToLowerInvariant();
            if (!Regex.IsMatch(t, "^[a-z0-9_]{1,100}$")) throw new DomainException("invalid_template", $"Nome do modelo de {field}: só letras minúsculas, números e _ (como está na Meta).");
            return t;
        }
        s.ChargeTemplate = Template(r.ChargeTemplate, s.ChargeTemplate, "cobrança");
        s.PromoTemplate = Template(r.PromoTemplate, s.PromoTemplate, "promoção");
        var lang = (r.Language ?? s.Language).Trim();
        if (!Regex.IsMatch(lang, "^[a-z]{2}(_[A-Z]{2})?$")) throw new DomainException("invalid_language", "Idioma do modelo no formato pt_BR.");
        s.Language = lang;
        if (r.DailyPromoLimit is < 1 or > MaxPromoLimit) throw new DomainException("invalid_limit", $"Limite diário de promoções entre 1 e {MaxPromoLimit}.");
        if (r.PromoIntervalDays is < MinIntervalDays or > 90) throw new DomainException("invalid_interval", $"Intervalo entre promoções para o mesmo cliente: de {MinIntervalDays} a 90 dias.");
        s.DailyPromoLimit = r.DailyPromoLimit; s.PromoIntervalDays = r.PromoIntervalDays;
        var connected = s.PhoneNumberId is not null && s.AccessTokenProtected is not null;
        if ((r.ChargesEnabled || r.PromotionsEnabled) && !connected)
            throw new DomainException("whatsapp_not_connected", "Conecte o WhatsApp da loja antes de ligar a cobrança ou as promoções.");
        s.ChargesEnabled = r.ChargesEnabled; s.PromotionsEnabled = r.PromotionsEnabled;
        s.UpdatedAt = Now; s.UpdatedByUserId = actor.UserId;
        audit.Add("whatsapp.settings_updated", actor.TenantId, nameof(TenantWhatsAppSettings), actor.TenantId,
            new { credentialsChanged, r.Disconnect, s.ChargesEnabled, s.PromotionsEnabled, s.DailyPromoLimit, s.PromoIntervalDays, s.ChargeTemplate, s.PromoTemplate });
        await db.SaveChangesAsync(ct);
        return await GetSettingsAsync();
    }

    private async Task<(TenantWhatsAppSettings S, string Token)> ConnectedAsync()
    {
        var s = await FindAsync();
        if (s is not { PhoneNumberId: not null, AccessTokenProtected: { } p })
            throw new DomainException("whatsapp_not_connected", "O WhatsApp da loja ainda não foi conectado (engrenagem > WhatsApp).", 409);
        return (s, Protector.Unprotect(p));
    }

    /// <summary>Teste com o modelo "hello_world" (já vem aprovado em toda conta nova da Meta).</summary>
    public async Task<object> TestAsync(WhatsAppTestRequest r, CancellationToken ct)
    {
        var (s, token) = await ConnectedAsync();
        var to = NormalizePhone(r.Phone) ?? throw new DomainException("invalid_phone", "Celular inválido. Use DDD + número.");
        var res = await wa.SendTemplateAsync(s.PhoneNumberId!, token, to, "hello_world", "en_US", [], ct);
        Record(null, null, WhatsAppKinds.Test, to, res, "Mensagem de teste (hello_world)", 0);
        await db.SaveChangesAsync(ct);
        if (!res.Ok) throw new DomainException("whatsapp_send_failed", res.Error ?? "Falha no envio.", 502);
        return new { sent = true };
    }

    private WhatsAppMessage Record(Guid? customerId, Guid? campaignId, string kind, string phone, WaSendResult res, string preview, long amount)
    {
        var m = new WhatsAppMessage
        {
            TenantId = actor.TenantId, CustomerId = customerId, CampaignId = campaignId, Kind = kind, Phone = phone, Status = res.Ok ? "sent" : "failed",
            ProviderMessageId = res.MessageId, Error = res.Error is null ? null : Param(res.Error, 500), Preview = Param(preview, 2000), AmountCents = amount, CreatedAt = Now,
        };
        db.WhatsAppMessages.Add(m);
        return m;
    }

    public async Task<object> MessagesAsync(string? kind)
    {
        var q = db.WhatsAppMessages.AsNoTracking();
        if (!string.IsNullOrEmpty(kind)) q = q.Where(m => m.Kind == kind);
        var list = await q.OrderByDescending(m => m.CreatedAt).Take(100).ToListAsync();
        var ids = list.Where(m => m.CustomerId != null).Select(m => m.CustomerId!.Value).Distinct().ToList();
        var names = await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
        return list.Select(m => new
        {
            m.Id, m.Kind, m.Status, m.CreatedAt, m.Error, m.Preview, m.AmountCents,
            customer = m.CustomerId is { } id && names.TryGetValue(id, out var n) ? n : null,
            phone = m.Phone.Length > 4 ? "•••" + m.Phone[^4..] : m.Phone,
        });
    }

    // ---------------- Cobrança do fiado ----------------

    public async Task SetBillingDayAsync(Guid customerId, int? day)
    {
        if (day is < 1 or > 31) throw new DomainException("invalid_day", "Dia de cobrança entre 1 e 31 (ou vazio para não cobrar).");
        var c = await lookup.GetAsync<Customer>(customerId);
        audit.Add("customer.billing_day", actor.TenantId, nameof(Customer), c.Id, new { before = c.BillingDay, after = day });
        c.BillingDay = day;
        await db.SaveChangesAsync();
    }

    private sealed record ChargeData(long OpenCents, string Items, string Text, string[] Params, List<string> Days, string Store, string First);

    /// <summary>Saldo do fiado e o que foi comprado em cada dia (das contas ainda em aberto).</summary>
    private async Task<ChargeData> BuildChargeAsync(Customer c, TenantWhatsAppSettings? s)
    {
        var titles = await db.FinancialTitles.AsNoTracking()
            .Where(t => t.CustomerId == c.Id && t.Kind == TitleKind.Receivable && t.Status == TitleStatus.Open && t.Category == CreditService.Category)
            .OrderBy(t => t.CreatedAt).Select(t => new { t.SaleId, t.AmountCents, t.CreatedAt }).ToListAsync();
        var open = await credit.OpenBalanceAsync(c.Id);
        var saleIds = titles.Where(t => t.SaleId != null).Select(t => t.SaleId!.Value).ToList();
        var sales = await db.Sales.AsNoTracking().Where(x => saleIds.Contains(x.Id))
            .Select(x => new { x.Id, x.ReceivedAt, items = x.Items.Select(i => new { i.ProductName, i.Quantity, i.TotalCents }) }).ToListAsync();
        var parts = new List<string>();
        foreach (var day in sales.OrderBy(x => x.ReceivedAt).GroupBy(x => Local(x.ReceivedAt).Date))
        {
            var items = day.SelectMany(x => x.items).Select(i =>
                $"{Param(i.ProductName, 40)} x{i.Quantity.ToString("0.###", Br)} ({Money(i.TotalCents)})");
            parts.Add($"{day.Key:dd/MM}: {string.Join(", ", items)}");
        }
        var itemsText = parts.Count == 0 ? "veja na loja" : Param(string.Join(" | ", parts), 600);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == c.TenantId);
        var first = Param(c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? c.Name, 40);
        string[] ps = [first, Param(tenant.TradeName, 60), Money(open), itemsText];
        var text = ChargeTemplateText.Replace("{{1}}", ps[0]).Replace("{{2}}", ps[1]).Replace("{{3}}", ps[2]).Replace("{{4}}", ps[3]);
        return new(open, itemsText, text, ps, parts, Param(tenant.TradeName, 60), first);
    }

    public async Task<ChargePreview> ChargePreviewAsync(Guid customerId)
    {
        var c = await lookup.GetAsync<Customer>(customerId);
        var s = await FindAsync();
        var data = await BuildChargeAsync(c, s);
        var phone = NormalizePhone(c.Phone);
        var reason = s is not { PhoneNumberId: not null, AccessTokenProtected: not null } ? "Conecte o WhatsApp da loja na engrenagem (Configurações > WhatsApp)."
            : phone is null ? "O cliente não tem celular válido no cadastro."
            : data.OpenCents <= 0 ? "O cliente não deve nada no crediário."
            : null;
        return new(reason is null, reason, phone is null ? null : "•••" + phone[^4..], data.OpenCents, data.Text, c.BillingDay, c.LastChargeSentOn);
    }

    /// <summary>Envia a cobrança agora (botão do WhatsApp na tela do cliente ou a rotina do dia de cobrança).</summary>
    public async Task<WhatsAppMessage> SendChargeAsync(Guid customerId, bool automatic, CancellationToken ct)
    {
        var (s, token) = await ConnectedAsync();
        var c = await lookup.GetAsync<Customer>(customerId);
        var phone = NormalizePhone(c.Phone) ?? throw new DomainException("invalid_phone", "O cliente não tem celular válido no cadastro.");
        var data = await BuildChargeAsync(c, s);
        if (data.OpenCents <= 0) throw new DomainException("nothing_to_charge", "O cliente não deve nada no crediário.");
        // Proteção contra clique repetido: no máximo uma cobrança por cliente a cada 12 horas.
        var since = Now.AddHours(-12);
        if (await db.WhatsAppMessages.AnyAsync(m => m.CustomerId == c.Id && m.Kind == WhatsAppKinds.Charge && m.Status == "sent" && m.CreatedAt > since, ct))
            throw new DomainException("charge_too_soon", "Este cliente já recebeu uma cobrança nas últimas 12 horas.", 409);
        var res = await wa.SendTemplateAsync(s.PhoneNumberId!, token, phone, s.ChargeTemplate, s.Language, data.Params, ct);
        var m = Record(c.Id, null, WhatsAppKinds.Charge, phone, res, data.Text, data.OpenCents);
        if (automatic) c.LastChargeSentOn = DateOnly.FromDateTime(Local(Now).Date);
        audit.Add(automatic ? "whatsapp.charge_auto" : "whatsapp.charge_sent", actor.TenantId, nameof(Customer), c.Id, new { amount = data.OpenCents, ok = res.Ok });
        await db.SaveChangesAsync(ct);
        if (!automatic && !res.Ok) throw new DomainException("whatsapp_send_failed", res.Error ?? "Falha no envio.", 502);
        return m;
    }

    // ---------------- Modo grátis: mensagem pronta aberta no WhatsApp da loja ----------------

    /// <summary>Link que abre o WhatsApp (celular ou computador) na conversa com o cliente e o texto já escrito.</summary>
    public static string WaLink(string phone, string text) => $"https://wa.me/{phone}?text={Uri.EscapeDataString(text)}";

    /// <summary>Cobrança do fiado para o dono mandar pelo WhatsApp da loja (sem custo): texto livre, com o que foi comprado em cada dia.</summary>
    public async Task<object> FreeChargeAsync(Guid customerId)
    {
        var c = await lookup.GetAsync<Customer>(customerId);
        var d = await BuildChargeAsync(c, null);
        var phone = NormalizePhone(c.Phone);
        var text = $"Olá, {d.First}! Aqui é da {d.Store}.\nSeu saldo no crediário é de {Money(d.OpenCents)}."
            + (d.Days.Count == 0 ? "" : "\n\nCompras em aberto:\n" + string.Join("\n", d.Days.Select(x => "• " + x)))
            + "\n\nPara pagar ou tirar dúvidas, é só responder esta mensagem.";
        var reason = phone is null ? "O cliente não tem celular válido no cadastro." : d.OpenCents <= 0 ? "O cliente não deve nada no crediário." : null;
        return new { canSend = reason is null, reason, phone, openCents = d.OpenCents, text, link = phone is null ? null : WaLink(phone, text) };
    }

    /// <summary>O dono abriu a cobrança no WhatsApp: registra (sai da lista do dia) e conta como a cobrança do mês.</summary>
    public async Task MarkFreeChargeAsync(Guid customerId)
    {
        var c = await lookup.GetAsync<Customer>(customerId);
        var d = await BuildChargeAsync(c, null);
        db.WhatsAppMessages.Add(new WhatsAppMessage
        {
            TenantId = actor.TenantId, CustomerId = c.Id, Kind = WhatsAppKinds.Charge, Phone = NormalizePhone(c.Phone) ?? "", Status = WhatsAppKinds.ManualStatus,
            Preview = Param(d.Text, 2000), AmountCents = d.OpenCents, CreatedAt = Now,
        });
        c.LastChargeSentOn = DateOnly.FromDateTime(Local(Now).Date);
        audit.Add("whatsapp.charge_manual", actor.TenantId, nameof(Customer), c.Id, new { amount = d.OpenCents });
        await db.SaveChangesAsync();
    }

    /// <summary>Dia de cobrança que cai no mês (dia 31 vira o último dia nos meses mais curtos).</summary>
    private static DateTime BillingDate(int billingDay, int year, int month) => new(year, month, Math.Min(billingDay, DateTime.DaysInMonth(year, month)));

    /// <summary>
    /// Cobranças do fiado: as de hoje que ainda não foram mandadas (inclui as atrasadas do mês) e a agenda dos próximos
    /// dias, para o app avisar o dono às 9h de cada dia de cobrança.
    /// </summary>
    public async Task<object> ChargesDueAsync()
    {
        var today = Local(Now).Date;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var list = await db.Customers.AsNoTracking().Where(c => c.IsActive && c.AnonymizedAt == null && c.BillingDay != null)
            .Select(c => new { c.Id, c.Name, c.Phone, c.BillingDay, c.LastChargeSentOn }).ToListAsync();
        var balances = new Dictionary<Guid, long>();
        foreach (var c in list) balances[c.Id] = await credit.OpenBalanceAsync(c.Id);
        var owing = list.Where(c => balances[c.Id] > 0).ToList();
        var due = owing.Where(c => BillingDate(c.BillingDay!.Value, today.Year, today.Month) <= today && (c.LastChargeSentOn == null || c.LastChargeSentOn < monthStart))
            .OrderBy(c => c.Name).Select(c => new { c.Id, c.Name, openCents = balances[c.Id], c.BillingDay, hasPhone = NormalizePhone(c.Phone) is not null }).ToList();
        var upcoming = Enumerable.Range(1, 31).Select(i => today.AddDays(i))
            .Select(day => new { date = DateOnly.FromDateTime(day), names = owing.Where(c => BillingDate(c.BillingDay!.Value, day.Year, day.Month) == day).Select(c => c.Name).OrderBy(n => n).ToList() })
            .Where(x => x.names.Count > 0).ToList();
        var auto = (await FindAsync()) is { ChargesEnabled: true, PhoneNumberId: not null, AccessTokenProtected: not null };
        return new { today = DateOnly.FromDateTime(today), automatic = auto, due, totalDueCents = due.Sum(x => x.openCents), upcoming };
    }

    /// <summary>Aviso do dia (aba Avisos e push) quando há cobranças para mandar à mão. No máximo um por dia.</summary>
    public async Task<bool> NotifyDueChargesAsync(NotificationService notifications, CancellationToken ct)
    {
        var s = await FindAsync();
        if (s is { ChargesEnabled: true, PhoneNumberId: not null, AccessTokenProtected: not null } || !InSendingHours(Now)) return false;
        var local = Local(Now);
        var start = new DateTimeOffset(local.Date, local.Offset);
        if (await db.Notifications.AnyAsync(n => n.Type == "fiado.charges_due" && n.CreatedAt >= start, ct)) return false;
        var today = local.Date;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var list = await db.Customers.AsNoTracking().Where(c => c.IsActive && c.AnonymizedAt == null && c.BillingDay != null
                && (c.LastChargeSentOn == null || c.LastChargeSentOn < monthStart)).Select(c => new { c.Id, c.Name, c.BillingDay }).ToListAsync(ct);
        var names = new List<string>();
        long total = 0;
        foreach (var c in list.Where(c => BillingDate(c.BillingDay!.Value, today.Year, today.Month) == today).OrderBy(c => c.Name))
        {
            var open = await credit.OpenBalanceAsync(c.Id);
            if (open <= 0) continue;
            names.Add(c.Name); total += open;
        }
        if (names.Count == 0) return false;
        var who = names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} e mais {names.Count - 3}";
        await notifications.CreateAsync(actor.TenantId, "fiado.charges_due", "Cobranças do fiado para hoje",
            $"{who} ({Money(total)}). Abra Clientes e toque em Cobrar: a mensagem já vai pronta no WhatsApp da loja.", new { count = names.Count, total }, ct);
        return true;
    }

    private static bool IsBillingDay(int billingDay, DateTime today) =>
        billingDay == today.Day || (billingDay > DateTime.DaysInMonth(today.Year, today.Month) && today.Day == DateTime.DaysInMonth(today.Year, today.Month));

    /// <summary>Só envia em horário comercial (padrão 9h às 20h, horário de Brasília).</summary>
    private int FromHour => config.GetValue("WhatsApp:SendFromHour", SendFromHour);
    private int UntilHour => config.GetValue("WhatsApp:SendUntilHour", SendUntilHour);
    private bool InSendingHours(DateTimeOffset utc) => Local(utc).Hour >= FromHour && Local(utc).Hour < UntilHour;

    /// <summary>Cobranças do dia: uma por cliente por mês, só com saldo em aberto e em horário comercial.</summary>
    public async Task<int> RunDueChargesAsync(CancellationToken ct)
    {
        var s = await FindAsync();
        if (s is not { ChargesEnabled: true, PhoneNumberId: not null, AccessTokenProtected: not null } || !InSendingHours(Now)) return 0;
        var today = Local(Now).Date;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var candidates = await db.Customers.Where(c => c.IsActive && c.AnonymizedAt == null && c.BillingDay != null && c.Phone != null
                && (c.LastChargeSentOn == null || c.LastChargeSentOn < monthStart))
            .Select(c => new { c.Id, c.BillingDay }).ToListAsync(ct);
        var sent = 0;
        foreach (var c in candidates.Where(c => IsBillingDay(c.BillingDay!.Value, today)).Take(200))
        {
            if (await credit.OpenBalanceAsync(c.Id) <= 0) continue;
            try { await SendChargeAsync(c.Id, automatic: true, ct); sent++; }
            catch (DomainException) { /* sem celular, cobrança recente etc.: segue para o próximo */ }
            await PaceAsync(ct);
        }
        return sent;
    }

    private Task PaceAsync(CancellationToken ct)
    {
        var ms = config.GetValue("WhatsApp:DelayMs", 2500);
        return ms > 0 ? Task.Delay(ms, ct) : Task.CompletedTask;
    }

    // ---------------- Campanhas de promoção ----------------

    private IQueryable<Customer> PromoAudience(int intervalDays)
    {
        var cutoff = Now.AddDays(-intervalDays);
        return db.Customers.Where(c => c.IsActive && c.AnonymizedAt == null && c.MarketingConsent && c.Phone != null
            && (c.LastPromoSentAt == null || c.LastPromoSentAt <= cutoff));
    }

    private async Task<int> SentTodayAsync(string kind, bool manual = false)
    {
        var local = Local(Now);
        var start = new DateTimeOffset(local.Date, local.Offset);
        return await db.WhatsAppMessages.CountAsync(m => m.Kind == kind && m.CreatedAt >= start && (m.Status == WhatsAppKinds.ManualStatus) == manual);
    }

    /// <summary>
    /// Teto do modo grátis: pelo WhatsApp comum, muitas mensagens iguais para quem não salvou o número da loja geram
    /// denúncias e bloqueio. Por isso no máximo 50 por dia, mesmo que o limite da API seja maior.
    /// </summary>
    public const int ManualPromoCap = 50;
    private static int ManualDailyLimit(TenantWhatsAppSettings? s) => Math.Min(ManualPromoCap, s?.DailyPromoLimit ?? ManualPromoCap);

    public async Task<object> CampaignsAsync()
    {
        var s = await FindAsync();
        var list = await db.WhatsAppCampaigns.AsNoTracking().OrderByDescending(c => c.ScheduledAt).Take(50).ToListAsync();
        var eligible = (await PromoAudience(s?.PromoIntervalDays ?? 7).Select(c => c.Phone).ToListAsync()).Count(p => NormalizePhone(p) is not null);
        var consenting = await db.Customers.CountAsync(c => c.IsActive && c.AnonymizedAt == null && c.MarketingConsent);
        return new
        {
            connected = s is { PhoneNumberId: not null, AccessTokenProtected: not null }, promotionsEnabled = s?.PromotionsEnabled ?? false,
            dailyLimit = s?.DailyPromoLimit ?? 200, intervalDays = s?.PromoIntervalDays ?? 7, sentToday = await SentTodayAsync(WhatsAppKinds.Promo),
            eligibleNow = eligible, consenting, sendFromHour = FromHour, sendUntilHour = UntilHour,
            manualDailyLimit = ManualDailyLimit(s), sentTodayManual = await SentTodayAsync(WhatsAppKinds.Promo, manual: true),
            campaigns = list.Select(c => new { c.Id, c.Title, c.Message, c.ScheduledAt, c.Status, c.Sent, c.Failed, c.StopReason, c.FinishedAt, c.Manual }),
        };
    }

    public async Task<object> CreateCampaignAsync(CampaignRequest r)
    {
        var s = await FindAsync();
        if (!r.Manual && s is not { PromotionsEnabled: true, PhoneNumberId: not null, AccessTokenProtected: not null })
            throw new DomainException("promotions_disabled", "Ligue as promoções na engrenagem (Configurações > WhatsApp) antes de criar uma campanha.", 409);
        var title = Param(r.Title ?? "", 100);
        var message = Param(r.Message ?? "", 500);
        if (title.Length < 3) throw new DomainException("invalid_title", "Dê um nome para a campanha (ex.: Promoção de sábado).");
        if (message.Length < 10) throw new DomainException("invalid_message", "Escreva a mensagem da promoção (10 a 500 letras).");
        if (Regex.Matches(message, "https?://").Count > 1) throw new DomainException("too_many_links", "Use no máximo um link na mensagem (muitos links parecem spam).");
        if (r.ScheduledAt < Now.AddMinutes(-5) || r.ScheduledAt > Now.AddDays(60)) throw new DomainException("invalid_date", "Agende para agora ou até 60 dias à frente.");
        // Limite anti-spam: uma campanha por dia e no máximo três a cada 7 dias.
        var day = Local(r.ScheduledAt).Date;
        var active = await db.WhatsAppCampaigns.Where(c => c.Status != CampaignStatus.Cancelled)
            .Select(c => c.ScheduledAt).ToListAsync();
        if (active.Any(a => Local(a).Date == day)) throw new DomainException("campaign_same_day", "Já existe uma campanha neste dia. Para não virar spam, é uma por dia.", 409);
        if (active.Count(a => Math.Abs((Local(a).Date - day).TotalDays) < 7) >= 3)
            throw new DomainException("campaign_week_limit", "Limite de 3 campanhas a cada 7 dias. Escolha outra data.", 409);
        var c = new WhatsAppCampaign { TenantId = actor.TenantId, Title = title, Message = message, ScheduledAt = r.ScheduledAt, Manual = r.Manual, CreatedByUserId = actor.UserId ?? Guid.Empty, CreatedAt = Now };
        db.WhatsAppCampaigns.Add(c);
        audit.Add("whatsapp.campaign_created", actor.TenantId, nameof(WhatsAppCampaign), c.Id, new { title, r.ScheduledAt, r.Manual });
        await db.SaveChangesAsync();
        return new { c.Id, c.Status };
    }

    public async Task CancelCampaignAsync(Guid id)
    {
        var c = await db.WhatsAppCampaigns.FirstOrDefaultAsync(x => x.Id == id) ?? throw new DomainException("not_found", "Campanha não encontrada.", 404);
        if (c.Status is CampaignStatus.Done or CampaignStatus.Cancelled) return;
        c.Status = CampaignStatus.Cancelled; c.FinishedAt = Now;
        audit.Add("whatsapp.campaign_cancelled", actor.TenantId, nameof(WhatsAppCampaign), c.Id, new { c.Sent });
        await db.SaveChangesAsync();
    }

    private static string PromoText(string first, string store, string message) =>
        $"Olá, {first}! Novidade da {store}: {message}\n\nSe não quiser mais receber ofertas, é só responder SAIR.";

    /// <summary>
    /// Campanha grátis: quem ainda falta receber hoje, cada um com o link do WhatsApp já com a mensagem.
    /// Respeita consentimento, intervalo entre promoções e o teto diário do modo grátis.
    /// </summary>
    public async Task<object> ManualQueueAsync(Guid id)
    {
        var camp = await db.WhatsAppCampaigns.FirstOrDefaultAsync(x => x.Id == id && x.Manual) ?? throw new DomainException("not_found", "Campanha não encontrada.", 404);
        var s = await FindAsync();
        var limit = ManualDailyLimit(s);
        var sentToday = await SentTodayAsync(WhatsAppKinds.Promo, manual: true);
        if (camp.Status is CampaignStatus.Cancelled or CampaignStatus.Done || camp.ScheduledAt > Now)
            return new { camp.Id, camp.Title, camp.Status, camp.Sent, dailyLimit = limit, sentToday, remaining = 0, queue = Array.Empty<object>() };
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId);
        var already = db.WhatsAppMessages.Where(m => m.CampaignId == camp.Id && m.CustomerId != null).Select(m => m.CustomerId!.Value);
        var audience = (await PromoAudience(s?.PromoIntervalDays ?? 7).Where(c => !already.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync())
            .Where(c => NormalizePhone(c.Phone) is not null).ToList();
        if (audience.Count == 0)
        {
            camp.Status = CampaignStatus.Done; camp.FinishedAt = Now;
            await db.SaveChangesAsync();
        }
        var store = Param(tenant.TradeName, 60);
        var queue = audience.Take(Math.Max(0, limit - sentToday)).Select(c =>
        {
            var first = Param(c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? c.Name, 40);
            return (object)new { customerId = c.Id, name = c.Name, link = WaLink(NormalizePhone(c.Phone)!, PromoText(first, store, camp.Message)) };
        }).ToList();
        return new { camp.Id, camp.Title, camp.Status, camp.Sent, dailyLimit = limit, sentToday, remaining = audience.Count, queue };
    }

    /// <summary>O dono abriu a promoção no WhatsApp para este cliente: conta no limite do dia e no intervalo dele.</summary>
    public async Task MarkManualSentAsync(Guid id, Guid customerId)
    {
        var camp = await db.WhatsAppCampaigns.FirstOrDefaultAsync(x => x.Id == id && x.Manual) ?? throw new DomainException("not_found", "Campanha não encontrada.", 404);
        if (camp.Status is CampaignStatus.Cancelled) throw new DomainException("campaign_cancelled", "Esta campanha foi cancelada.", 409);
        var c = await lookup.GetAsync<Customer>(customerId);
        if (!c.MarketingConsent) throw new DomainException("no_consent", "Este cliente não aceitou receber ofertas.", 409);
        if (await db.WhatsAppMessages.AnyAsync(m => m.CampaignId == id && m.CustomerId == customerId)) return;
        var s = await FindAsync();
        if (await SentTodayAsync(WhatsAppKinds.Promo, manual: true) >= ManualDailyLimit(s))
            throw new DomainException("daily_limit", "Limite de promoções de hoje atingido. Continue amanhã para o número não ser bloqueado.", 409);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId);
        var first = Param(c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? c.Name, 40);
        db.WhatsAppMessages.Add(new WhatsAppMessage
        {
            TenantId = actor.TenantId, CustomerId = c.Id, CampaignId = id, Kind = WhatsAppKinds.Promo, Phone = NormalizePhone(c.Phone) ?? "",
            Status = WhatsAppKinds.ManualStatus, Preview = Param(PromoText(first, Param(tenant.TradeName, 60), camp.Message), 2000), CreatedAt = Now,
        });
        camp.Sent++;
        if (camp.Status == CampaignStatus.Scheduled) camp.Status = CampaignStatus.Sending;
        c.LastPromoSentAt = Now;
        await db.SaveChangesAsync();
    }

    /// <summary>Aviso (aba Avisos e push) quando chega a hora de uma campanha grátis. Um por campanha.</summary>
    public async Task<int> NotifyManualCampaignsAsync(NotificationService notifications, CancellationToken ct)
    {
        if (!InSendingHours(Now)) return 0;
        var now = Now;
        var due = await db.WhatsAppCampaigns.Where(c => c.Manual && c.Status == CampaignStatus.Scheduled && c.ScheduledAt <= now).ToListAsync(ct);
        foreach (var c in due)
        {
            c.Status = CampaignStatus.Sending;
            await db.SaveChangesAsync(ct);
            await notifications.CreateAsync(actor.TenantId, "whatsapp.campaign_due", "Hora de mandar a promoção",
                $"\"{c.Title}\": abra Promoções e toque em Mandar. A mensagem já vai pronta no WhatsApp da loja.", new { campaignId = c.Id }, ct);
        }
        return due.Count;
    }

    /// <summary>Envia as campanhas que já chegaram na hora, aos poucos, respeitando o teto diário.</summary>
    public async Task<int> RunDueCampaignsAsync(CancellationToken ct)
    {
        var s = await FindAsync();
        if (s is not { PromotionsEnabled: true, PhoneNumberId: not null, AccessTokenProtected: { } p } || !InSendingHours(Now)) return 0;
        var token = Protector.Unprotect(p);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == actor.TenantId, ct);
        var now = Now;
        var due = await db.WhatsAppCampaigns.Where(c => !c.Manual && (c.Status == CampaignStatus.Scheduled || c.Status == CampaignStatus.Sending) && c.ScheduledAt <= now)
            .OrderBy(c => c.ScheduledAt).ToListAsync(ct);
        var total = 0;
        foreach (var camp in due)
        {
            camp.Status = CampaignStatus.Sending;
            var room = s.DailyPromoLimit - await SentTodayAsync(WhatsAppKinds.Promo);
            if (room <= 0) { await db.SaveChangesAsync(ct); break; } // continua amanhã
            var already = db.WhatsAppMessages.Where(m => m.CampaignId == camp.Id && m.CustomerId != null).Select(m => m.CustomerId!.Value);
            var audience = (await PromoAudience(s.PromoIntervalDays).Where(c => !already.Contains(c.Id)).OrderBy(c => c.Id).ToListAsync(ct))
                .Where(c => NormalizePhone(c.Phone) is not null).ToList();
            if (audience.Count == 0) { camp.Status = CampaignStatus.Done; camp.FinishedAt = Now; await db.SaveChangesAsync(ct); continue; }
            foreach (var c in audience.Take(room))
            {
                var phone = NormalizePhone(c.Phone)!;
                var first = Param(c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? c.Name, 40);
                string[] ps = [first, Param(tenant.TradeName, 60), camp.Message];
                var res = await wa.SendTemplateAsync(s.PhoneNumberId!, token, phone, s.PromoTemplate, s.Language, ps, ct);
                Record(c.Id, camp.Id, WhatsAppKinds.Promo, phone, res, $"Olá, {ps[0]}! Novidade da {ps[1]}: {ps[2]}", 0);
                if (res.Ok) { camp.Sent++; c.LastPromoSentAt = Now; total++; } else camp.Failed++;
                // Muitas falhas seguidas derrubam a qualidade do número na Meta: para e avisa o dono.
                var tries = camp.Sent + camp.Failed;
                if (tries >= 10 && camp.Failed * 10 > tries * 3)
                {
                    camp.Status = CampaignStatus.Stopped; camp.FinishedAt = Now;
                    camp.StopReason = "Parada automática: mais de 30% das mensagens falharam. Último erro: " + Param(res.Error ?? "", 200);
                    audit.Add("whatsapp.campaign_stopped", actor.TenantId, nameof(WhatsAppCampaign), camp.Id, new { camp.Sent, camp.Failed });
                }
                await db.SaveChangesAsync(ct);
                if (camp.Status == CampaignStatus.Stopped) break;
                await PaceAsync(ct);
            }
            if (camp.Status == CampaignStatus.Sending && audience.Count <= room) { camp.Status = CampaignStatus.Done; camp.FinishedAt = Now; await db.SaveChangesAsync(ct); }
        }
        return total;
    }

    // ---------------- Avisos recebidos da Meta (cliente respondeu SAIR) ----------------

    public async Task<string?> VerifyWebhookAsync(string? mode, string? verifyToken, string? challenge)
    {
        var s = await FindAsync();
        return mode == "subscribe" && s is { WebhookVerifyToken.Length: > 0 } && challenge is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(verifyToken ?? ""), Encoding.UTF8.GetBytes(s.WebhookVerifyToken)) ? challenge : null;
    }

    /// <summary>Confere a assinatura da Meta e descadastra das promoções quem respondeu SAIR (LGPD e proteção do número).</summary>
    public async Task<bool> HandleWebhookAsync(string body, string? signature)
    {
        var s = await FindAsync();
        if (s?.AppSecretProtected is not { } secretP || signature is null || !signature.StartsWith("sha256=")) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Protector.Unprotect(secretP)), Encoding.UTF8.GetBytes(body));
        byte[] given;
        try { given = Convert.FromHexString(signature[7..]); } catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return false;
        JsonNode? root;
        try { root = JsonNode.Parse(body); } catch (System.Text.Json.JsonException) { return true; }
        var optOuts = new List<string>();
        foreach (var entry in root?["entry"]?.AsArray() ?? [])
            foreach (var change in entry?["changes"]?.AsArray() ?? [])
                foreach (var msg in change?["value"]?["messages"]?.AsArray() ?? [])
                {
                    var text = msg?["text"]?["body"]?.GetValue<string>()?.Trim().ToUpperInvariant() ?? msg?["button"]?["text"]?.GetValue<string>()?.Trim().ToUpperInvariant();
                    var from = NormalizePhone(msg?["from"]?.GetValue<string>());
                    if (from is not null && text is not null && OptOutWords.Contains(text.TrimEnd('.', '!'))) optOuts.Add(from);
                }
        if (optOuts.Count == 0) return true;
        var customers = await db.Customers.Where(c => c.MarketingConsent && c.Phone != null).ToListAsync();
        foreach (var c in customers.Where(c => optOuts.Contains(NormalizePhone(c.Phone) ?? "")))
        {
            c.MarketingConsent = false; c.ConsentAt = Now;
            audit.Add("customer.marketing_optout", actor.TenantId, nameof(Customer), c.Id, new { via = "whatsapp" });
        }
        await db.SaveChangesAsync();
        return true;
    }
}

/// <summary>Rotina do WhatsApp: cobranças do dia e campanhas agendadas, a cada 15 minutos (só em horário comercial).</summary>
public sealed class WhatsAppJob(IServiceProvider sp, IConfiguration config, ILogger<WhatsAppJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("WhatsApp:AutoRun", true)) return;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(config.GetValue("WhatsApp:IntervalMinutes", 15)), ct); } catch (OperationCanceledException) { return; }
            try { await RunOnceAsync(sp, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha na rotina do WhatsApp"); }
        }
    }

    public static async Task<Dictionary<Guid, (int Charges, int Promos)>> RunOnceAsync(IServiceProvider root, CancellationToken ct)
    {
        List<Guid> tenants;
        using (var scope = root.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetPlatformScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            tenants = await db.WhatsAppSettings.IgnoreQueryFilters()
                .Where(s => s.PhoneNumberId != null && (s.ChargesEnabled || s.PromotionsEnabled)).Select(s => s.TenantId).ToListAsync(ct);
            // Modo grátis: lojas com dia de cobrança ou campanha grátis recebem o aviso para mandar à mão.
            tenants = tenants
                .Concat(await db.Customers.IgnoreQueryFilters().Where(c => c.BillingDay != null && c.IsActive).Select(c => c.TenantId).Distinct().ToListAsync(ct))
                .Concat(await db.WhatsAppCampaigns.IgnoreQueryFilters().Where(c => c.Manual && c.Status == CampaignStatus.Scheduled).Select(c => c.TenantId).Distinct().ToListAsync(ct))
                .Distinct().ToList();
        }
        var result = new Dictionary<Guid, (int, int)>();
        foreach (var t in tenants)
        {
            using var scope = root.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(t);
            var actor = scope.ServiceProvider.GetRequiredService<RequestActor>();
            actor.TenantId = t; actor.Type = ActorType.System;
            var svc = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
            try
            {
                result[t] = (await svc.RunDueChargesAsync(ct), await svc.RunDueCampaignsAsync(ct));
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
                await svc.NotifyDueChargesAsync(notifications, ct);
                await svc.NotifyManualCampaignsAsync(notifications, ct);
            }
            catch (DomainException ex) { scope.ServiceProvider.GetRequiredService<ILogger<WhatsAppJob>>().LogWarning("WhatsApp ({Tenant}): {Error}", t, ex.Message); }
        }
        return result;
    }
}
