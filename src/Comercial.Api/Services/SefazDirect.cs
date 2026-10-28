using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using Comercial.Domain;
using Unimake.Business.DFe.Servicos;
using Unimake.Business.DFe.Xml.NFe;
using UniNFCe = Unimake.Business.DFe.Servicos.NFCe;

namespace Comercial.Api.Services;

/// <summary>
/// Envio ao webservice da SEFAZ. Separado para os testes simularem a resposta: o XML continua sendo montado,
/// assinado e validado no schema oficial pela biblioteca, só a ida à SEFAZ é trocada.
/// </summary>
public interface ISefazTransport
{
    /// <summary>Executa o serviço já montado/assinado e devolve o XML de retorno da SEFAZ.</summary>
    Task<string> SendAsync(ServicoBase service, CancellationToken ct);
}

public sealed class UnimakeSefazTransport : ISefazTransport
{
    public Task<string> SendAsync(ServicoBase service, CancellationToken ct) => Task.Run(() =>
    {
        service.Executar();
        return service.RetornoWSString;
    }, ct);
}

/// <summary>
/// Emissão DIRETA da NFC-e (modelo 65) na SEFAZ do estado, com o certificado A1 da loja e a biblioteca aberta Unimake.DFe (MIT).
/// Sem provedor pago: o XML autorizado já volta pronto para o arquivo fiscal.
/// STATUS: XML validado contra o schema oficial nos testes; transmissão real só pode ser conferida com certificado e CNPJ
/// de uma loja, primeiro em HOMOLOGAÇÃO.
/// </summary>
public sealed class SefazNfceProvider(ISefazTransport transport, IConfiguration config, ILogger<SefazNfceProvider> log) : IFiscalProvider
{
    public const string ProviderName = "sefaz";
    public string Name => ProviderName;
    private const string Ns = "http://www.portalfiscal.inf.br/nfe";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string HomologationText = "NOTA FISCAL EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL";

    private static TipoAmbiente Amb(FiscalContext c) => c.Profile.Environment == 1 ? TipoAmbiente.Producao : TipoAmbiente.Homologacao;
    private static decimal D(JsonNode? n) => decimal.Parse(n!.ToString(), Inv);
    private static string S(JsonNode? n) => n?.ToString() ?? "";

    public Configuracao Config(FiscalContext c) => new()
    {
        TipoDFe = TipoDFe.NFCe,
        TipoEmissao = TipoEmissao.Normal,
        CertificadoDigital = c.Certificate ?? throw new DomainException("fiscal_certificate_missing", "Envie o certificado digital A1 da loja."),
        CSC = c.Csc ?? "",
        CSCIDToken = int.TryParse(c.Profile.CscId, out var id) ? id : 0,
        VersaoQRCodeNFCe = c.Profile.QrCodeVersion,
    };

    // ---------------- Montagem ----------------

    private static ICMS Icms(string cst, int origin, TaxRegime regime)
    {
        var orig = (OrigemMercadoria)origin;
        if (regime is TaxRegime.SimplesNacional or TaxRegime.SimplesExcessoSublimite or TaxRegime.Mei)
            return cst switch
            {
                "102" or "103" or "300" or "400" => new ICMS { ICMSSN102 = new ICMSSN102 { Orig = orig, CSOSN = cst } },
                "500" => new ICMS { ICMSSN500 = new ICMSSN500 { Orig = orig, CSOSN = cst } },
                _ => throw new DomainException("fiscal_cst_unsupported", $"CSOSN {cst} ainda não é suportado na emissão direta (use 102, 103, 300, 400 ou 500)."),
            };
        return cst switch
        {
            "40" or "41" or "50" => new ICMS { ICMS40 = new ICMS40 { Orig = orig, CST = cst } },
            "60" => new ICMS { ICMS60 = new ICMS60 { Orig = orig, CST = cst } },
            _ => throw new DomainException("fiscal_cst_unsupported", $"CST {cst} exige alíquota de ICMS, ainda não cadastrada no produto (suportados: 40, 41, 50, 60)."),
        };
    }

    private static bool NoTaxCst(string cst) => cst is "04" or "05" or "06" or "07" or "08" or "09";

    private static MeioPagamento Payment(string code) => code switch
    {
        "01" => MeioPagamento.Dinheiro,
        "03" => MeioPagamento.CartaoCredito,
        "04" => MeioPagamento.CartaoDebito,
        "05" => MeioPagamento.CreditoLoja,
        "17" => MeioPagamento.PagamentoInstantaneo,
        _ => MeioPagamento.Outros,
    };

    /// <summary>Monta a NFC-e a partir dos dados da venda (mesmo formato usado para o provedor) e do emitente.</summary>
    public EnviNFe Build(FiscalContext c, FiscalDocument doc, JsonObject payload)
    {
        var p = c.Profile;
        if (!Enum.TryParse<UFBrasil>(p.Uf, out var uf)) throw new DomainException("invalid_uf", "UF do emitente inválida.");
        var homolog = p.Environment != 1;
        var dets = new List<Det>();
        double vProd = 0, vDesc = 0;
        var n = 0;
        foreach (var i in payload["items"]!.AsArray())
        {
            n++;
            var qty = D(i!["quantidade_comercial"]);
            var unit = D(i["valor_unitario_comercial"]);
            var bruto = (double)D(i["valor_bruto"]);
            var desc = (double)D(i["valor_desconto"]);
            vProd += bruto; vDesc += desc;
            var pisCst = S(i["pis_situacao_tributaria"]);
            var cofinsCst = S(i["cofins_situacao_tributaria"]);
            dets.Add(new Det
            {
                NItem = n,
                Prod = new Prod
                {
                    CProd = S(i["codigo_produto"]), CEAN = "SEM GTIN", CEANTrib = "SEM GTIN",
                    // Regra da SEFAZ: em homologação a 1ª descrição precisa ser este texto.
                    XProd = homolog && n == 1 ? HomologationText : S(i["descricao"]),
                    NCM = S(i["codigo_ncm"]), CEST = i["cest"] is { } cest ? S(cest) : null, CFOP = S(i["cfop"]),
                    UCom = S(i["unidade_comercial"]), QCom = qty, VUnCom = unit, VProd = bruto,
                    UTrib = S(i["unidade_tributavel"]), QTrib = qty, VUnTrib = unit,
                    VDesc = desc > 0 ? desc : 0, IndTot = SimNao.Sim,
                },
                Imposto = new Imposto
                {
                    ICMS = Icms(S(i["icms_situacao_tributaria"]), int.Parse(S(i["icms_origem"]), Inv), p.Regime),
                    PIS = NoTaxCst(pisCst) ? new PIS { PISNT = new PISNT { CST = pisCst } } : new PIS { PISOutr = new PISOutr { CST = pisCst, VBC = 0, PPIS = 0, VPIS = 0 } },
                    COFINS = NoTaxCst(cofinsCst) ? new COFINS { COFINSNT = new COFINSNT { CST = cofinsCst } } : new COFINS { COFINSOutr = new COFINSOutr { CST = cofinsCst, VBC = 0, PCOFINS = 0, VCOFINS = 0 } },
                },
            });
        }
        vProd = Math.Round(vProd, 2); vDesc = Math.Round(vDesc, 2);
        var pays = payload["formas_pagamento"]!.AsArray().Select(x =>
        {
            var tp = Payment(S(x!["forma_pagamento"]));
            var dp = new DetPag { IndPag = IndicadorPagamento.PagamentoVista, TPag = tp, VPag = (double)D(x["valor_pagamento"]) };
            if (tp is MeioPagamento.CartaoCredito or MeioPagamento.CartaoDebito or MeioPagamento.PagamentoInstantaneo)
                dp.Card = new Card { TpIntegra = TipoIntegracaoPagamento.PagamentoNaoIntegrado };
            if (tp == MeioPagamento.Outros) dp.XPag = "Outros";
            return dp;
        }).ToList();
        var crt = p.Regime switch
        {
            TaxRegime.SimplesNacional => CRT.SimplesNacional, TaxRegime.SimplesExcessoSublimite => CRT.SimplesNacionalExcessoSublimite,
            TaxRegime.Mei => CRT.SimplesNacionalMEI, _ => CRT.RegimeNormal,
        };
        var inf = new InfNFe
        {
            Versao = "4.00",
            Ide = new Ide
            {
                CUF = uf, CNF = doc.NumericCode, NatOp = "VENDA AO CONSUMIDOR", Mod = ModeloDFe.NFCe, Serie = doc.Series ?? p.NfceSeries, NNF = doc.Number ?? 0,
                DhEmi = (doc.EmissionAt ?? DateTimeOffset.UtcNow).LocalDateTime, TpNF = TipoOperacao.Saida, IdDest = DestinoOperacao.OperacaoInterna,
                CMunFG = p.CityCode, TpImp = FormatoImpressaoDANFE.NFCe, TpEmis = TipoEmissao.Normal, TpAmb = homolog ? TipoAmbiente.Homologacao : TipoAmbiente.Producao,
                FinNFe = FinalidadeNFe.Normal, IndFinal = SimNao.Sim, IndPres = IndicadorPresenca.OperacaoPresencial, ProcEmi = ProcessoEmissao.AplicativoContribuinte,
                VerProc = "Comercial 1.0",
            },
            Emit = new Emit
            {
                CNPJ = p.Cnpj, XNome = homolog ? HomologationText : p.LegalName, XFant = string.IsNullOrWhiteSpace(p.TradeName) ? null : p.TradeName,
                EnderEmit = new EnderEmit
                {
                    XLgr = p.Street, Nro = p.AddressNumber, XBairro = p.District, CMun = p.CityCode, XMun = p.CityName, UF = uf, CEP = p.Cep,
                    Fone = string.IsNullOrWhiteSpace(p.Phone) ? null : p.Phone,
                },
                IE = p.StateRegistration, CRT = crt,
            },
            Det = dets,
            Total = new Total
            {
                ICMSTot = new ICMSTot
                {
                    VBC = 0, VICMS = 0, VICMSDeson = 0, VFCP = 0, VBCST = 0, VST = 0, VFCPST = 0, VFCPSTRet = 0, VProd = vProd, VFrete = 0, VSeg = 0,
                    VDesc = vDesc, VII = 0, VIPI = 0, VIPIDevol = 0, VPIS = 0, VCOFINS = 0, VOutro = 0, VNF = Math.Round(vProd - vDesc, 2),
                },
            },
            Transp = new Transp { ModFrete = ModalidadeFrete.SemOcorrenciaTransporte },
            Pag = new Pag { DetPag = pays, VTroco = (double)D(payload["valor_troco"]) is > 0 and var troco ? troco : 0 },
        };
        // Responsável técnico (exigido em vários estados): CNPJ de quem desenvolve o sistema, configurado no servidor.
        if (config["Fiscal:RespTec:Cnpj"] is { Length: 14 } rt)
            inf.InfRespTec = new InfRespTec { CNPJ = rt, XContato = config["Fiscal:RespTec:Contato"] ?? "Suporte", Email = config["Fiscal:RespTec:Email"] ?? "", Fone = config["Fiscal:RespTec:Fone"] ?? "" };
        return new EnviNFe { Versao = "4.00", IdLote = (doc.Number ?? 1).ToString("000000000000000"), IndSinc = SimNao.Sim, NFe = [new NFe { InfNFeField = inf }] };
    }

    // ---------------- Retornos ----------------

    private static XmlDocument Parse(string xml)
    {
        var d = new XmlDocument { XmlResolver = null };
        using var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        d.Load(r);
        return d;
    }
    private static XmlNode? Node(XmlNode d, string localName) => d.SelectSingleNode($"//*[local-name()='{localName}']");
    private static string? Text(XmlNode? n, string localName) => n?.SelectSingleNode($".//*[local-name()='{localName}']")?.InnerText;
    private static int Code(XmlNode? n, string localName = "cStat") => int.TryParse(Text(n, localName), out var c) ? c : 0;

    /// <summary>Junta o documento assinado com o protocolo da SEFAZ (nfeProc, procEventoNFe, procInutNFe) — é o XML que vale.</summary>
    public static byte[] Proc(string root, string version, params XmlNode[] parts)
    {
        var d = new XmlDocument();
        var r = d.CreateElement(root, Ns);
        r.SetAttribute("versao", version);
        d.AppendChild(r);
        foreach (var part in parts) r.AppendChild(d.ImportNode(part, true));
        return Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + d.DocumentElement!.OuterXml);
    }

    private static readonly int[] Authorized = [100, 150];
    private static readonly int[] Denied = [110, 301, 302, 303];
    private static readonly int[] TryLater = [103, 105, 108, 109, 999];

    private FiscalProviderResult FromProtocol(XmlNode prot, XmlNode signedNfe, string key, int? number, int? series, string? qr, string? url)
    {
        var cStat = Code(prot);
        var motivo = Text(prot, "xMotivo") ?? "";
        var nProt = Text(prot, "nProt");
        if (Authorized.Contains(cStat) || Denied.Contains(cStat))
        {
            var xml = Proc("nfeProc", "4.00", signedNfe, prot);
            return new(Authorized.Contains(cStat) ? FiscalStatus.Authorized : FiscalStatus.Denied, $"{cStat} - {motivo}", nProt, key, number, series,
                QrCodeUrl: qr, Xml: xml, ConsultUrl: url);
        }
        if (cStat == 539) return new(FiscalStatus.Rejected, $"{cStat} - {motivo}", NeedsNewNumber: true);
        return new(FiscalStatus.Rejected, $"{cStat} - {motivo}");
    }

    public async Task<FiscalProviderResult> TransmitAsync(FiscalContext c, FiscalDocument doc, JsonObject payload, CancellationToken ct)
    {
        UniNFCe.Autorizacao service;
        var cfg = Config(c);
        try { service = new UniNFCe.Autorizacao(Build(c, doc, payload), cfg); }
        catch (DomainException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Falha de schema/assinatura: o problema está nos dados (cadastro), não na SEFAZ.
            log.LogWarning(ex, "NFC-e {Doc}: XML recusado na validação local", doc.Id);
            return new(FiscalStatus.Rejected, "XML recusado na validação do schema: " + (ex.InnerException?.Message ?? ex.Message));
        }
        var signed = service.ConteudoXMLAssinado;
        var nfe = Node(signed, "NFe")!;
        var key = (Node(nfe, "infNFe") as XmlElement)?.GetAttribute("Id").Replace("NFe", "") ?? "";
        var qr = Node(signed, "qrCode")?.InnerText.Trim();
        var url = Node(signed, "urlChave")?.InnerText.Trim();
        try
        {
            // Nova tentativa: pergunta antes se a SEFAZ já autorizou esta chave (evita nota em duplicidade).
            if (doc.Attempts > 1)
            {
                var q = Parse(await transport.SendAsync(new UniNFCe.ConsultaProtocolo(new ConsSitNFe { Versao = "4.00", TpAmb = Amb(c), ChNFe = key }, Config(c)), ct));
                var qs = Code(Node(q, "retConsSitNFe"));
                if ((Authorized.Contains(qs) || Denied.Contains(qs)) && Node(q, "protNFe") is { } qp)
                    return FromProtocol(qp, nfe, key, doc.Number, doc.Series, qr, url);
            }
            var ret = Parse(await transport.SendAsync(service, ct));
            var lote = Node(ret, "retEnviNFe");
            var loteStat = Code(lote);
            if (loteStat == 104 && Node(ret, "protNFe") is { } prot)
            {
                if (Code(prot) == 204) // duplicidade: já autorizada antes; busca o protocolo
                {
                    var q = Parse(await transport.SendAsync(new UniNFCe.ConsultaProtocolo(new ConsSitNFe { Versao = "4.00", TpAmb = Amb(c), ChNFe = key }, Config(c)), ct));
                    if (Node(q, "protNFe") is { } qp) return FromProtocol(qp, nfe, key, doc.Number, doc.Series, qr, url);
                }
                return FromProtocol(prot, nfe, key, doc.Number, doc.Series, qr, url);
            }
            var motivo = $"{loteStat} - {Text(lote, "xMotivo")}";
            return TryLater.Contains(loteStat)
                ? new(FiscalStatus.PendingTransmission, "SEFAZ: " + motivo + " (nova tentativa automática)", Retry: true)
                : new(FiscalStatus.Rejected, motivo);
        }
        catch (Exception ex) when (ex is not DomainException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "NFC-e {Doc}: falha de comunicação com a SEFAZ", doc.Id);
            return new(FiscalStatus.PendingTransmission, "Sem resposta da SEFAZ; nova tentativa automática.", Retry: true);
        }
    }

    public async Task<FiscalProviderResult> CancelAsync(FiscalContext c, FiscalDocument doc, string reason, CancellationToken ct)
    {
        if (!Enum.TryParse<UFBrasil>(c.Profile.Uf, out var uf)) throw new DomainException("invalid_uf", "UF do emitente inválida.");
        var env = new EnvEvento
        {
            Versao = "1.00", IdLote = "000000000000001",
            Evento = [new Evento
            {
                Versao = "1.00",
                InfEvento = new Unimake.Business.DFe.Xml.NFe.InfEvento(new DetEventoCanc { NProt = doc.Protocol!, Versao = "1.00", XJust = reason })
                {
                    COrgao = uf, ChNFe = doc.AccessKey!, CNPJ = c.Profile.Cnpj, DhEvento = DateTime.Now, TpEvento = TipoEventoNFe.Cancelamento, NSeqEvento = 1,
                    VerEvento = "1.00", TpAmb = Amb(c),
                },
            }],
        };
        var service = new UniNFCe.RecepcaoEvento(env, Config(c));
        string raw;
        try { raw = await transport.SendAsync(service, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Cancelamento {Doc}: falha de comunicação", doc.Id);
            return new(doc.Status, "Sem resposta da SEFAZ. Tente de novo em instantes.");
        }
        var ret = Parse(raw);
        var retEvento = Node(ret, "retEvento");
        var cStat = Code(retEvento);
        var motivo = $"{cStat} - {Text(retEvento, "xMotivo") ?? Text(Node(ret, "retEnvEvento"), "xMotivo")}";
        if (cStat is 135 or 155 && retEvento is not null)
            return new(FiscalStatus.Cancelled, motivo, Text(retEvento, "nProt"), Xml: Proc("procEventoNFe", "1.00", Node(service.ConteudoXMLAssinado, "evento")!, retEvento));
        return new(doc.Status, motivo);
    }

    public async Task<FiscalProviderResult> VoidNumbersAsync(FiscalContext c, int series, int first, int last, string reason, CancellationToken ct)
    {
        if (!Enum.TryParse<UFBrasil>(c.Profile.Uf, out var uf)) throw new DomainException("invalid_uf", "UF do emitente inválida.");
        var inut = new InutNFe
        {
            Versao = "4.00",
            InfInut = new InutNFeInfInut
            {
                Ano = (DateTime.Now.Year % 100).ToString("00"), CNPJ = c.Profile.Cnpj, CUF = uf, Mod = ModeloDFe.NFCe, NNFIni = first, NNFFin = last, Serie = series,
                TpAmb = Amb(c), XJust = reason,
            },
        };
        var service = new UniNFCe.Inutilizacao(inut, Config(c));
        string raw;
        try { raw = await transport.SendAsync(service, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Inutilização: falha de comunicação");
            return new(FiscalStatus.Rejected, "Sem resposta da SEFAZ. Tente de novo em instantes.");
        }
        var ret = Parse(raw);
        var r = Node(ret, "retInutNFe")!;
        var cStat = Code(r);
        var motivo = $"{cStat} - {Text(r, "xMotivo")}";
        return cStat == 102
            ? new(FiscalStatus.Authorized, motivo, Text(r, "nProt"), Xml: Proc("procInutNFe", "4.00", Node(service.ConteudoXMLAssinado, "inutNFe")!, r))
            : new(FiscalStatus.Rejected, motivo);
    }

    /// <summary>Na emissão direta o XML já volta na autorização; não há o que baixar.</summary>
    public Task<byte[]?> DownloadXmlAsync(FiscalContext c, string url, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    // ---------------- Certificado ----------------

    /// <summary>Lê o .pfx e confere: chave privada, validade e CNPJ (raiz) do certificado igual ao da loja.</summary>
    public static X509Certificate2 LoadCertificate(byte[] pfx, string password, string? expectedCnpj, DateTimeOffset now)
    {
        X509Certificate2 cert;
        try { cert = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable); }
        catch (CryptographicException) { throw new DomainException("certificate_invalid", "Não foi possível abrir o certificado: arquivo .pfx inválido ou senha errada."); }
        if (!cert.HasPrivateKey) throw new DomainException("certificate_invalid", "O arquivo não tem a chave privada (use o .pfx do certificado A1).");
        if (cert.NotAfter.ToUniversalTime() < now.UtcDateTime) throw new DomainException("certificate_expired", $"Certificado vencido em {cert.NotAfter:dd/MM/yyyy}.");
        var cnpj = CnpjOf(cert);
        if (expectedCnpj is { Length: 14 } && cnpj is { Length: 14 } && cnpj[..8] != expectedCnpj[..8])
            throw new DomainException("certificate_cnpj_mismatch", $"O certificado é do CNPJ {cnpj}, diferente do CNPJ da loja.");
        return cert;
    }

    /// <summary>CNPJ do e-CNPJ ICP-Brasil: fica no nome comum, depois dos dois-pontos ("RAZAO SOCIAL:12345678000199").</summary>
    public static string? CnpjOf(X509Certificate2 cert)
    {
        var cn = cert.GetNameInfo(X509NameType.SimpleName, false) ?? "";
        var tail = cn.Contains(':') ? cn[(cn.LastIndexOf(':') + 1)..] : cn;
        var digits = new string(tail.Where(char.IsDigit).ToArray());
        return digits.Length == 14 ? digits : null;
    }
}
