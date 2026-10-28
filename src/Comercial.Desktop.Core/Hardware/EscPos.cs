using System.Text;

namespace Comercial.Desktop.Core.Hardware;

/// <summary>
/// Monta comandos ESC/POS (Epson, Elgin, Bematech, Daruma, Tanca e compatíveis) para impressoras térmicas de 58/80 mm:
/// texto em página de código 860 (português), negrito, alinhamento, corte, QR Code e pulso da gaveta de dinheiro.
/// </summary>
public sealed class EscPosBuilder
{
    private readonly List<byte> _bytes = [];
    private readonly Encoding _enc;
    public int Columns { get; }

    static EscPosBuilder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public EscPosBuilder(int paperMm = 80)
    {
        Columns = paperMm <= 58 ? 32 : 48;
        _enc = Encoding.GetEncoding(860);
        Raw(0x1B, 0x40);        // ESC @  — inicializa
        Raw(0x1B, 0x74, 3);     // ESC t 3 — página de código 860 (português)
    }

    public EscPosBuilder Raw(params byte[] b) { _bytes.AddRange(b); return this; }
    public EscPosBuilder Text(string s) { _bytes.AddRange(_enc.GetBytes(s)); return this; }
    public EscPosBuilder Line(string s = "") => Text(s).Raw(0x0A);
    public EscPosBuilder Bold(bool on) => Raw(0x1B, 0x45, (byte)(on ? 1 : 0));
    public EscPosBuilder Align(int a) => Raw(0x1B, 0x61, (byte)a); // 0 esquerda, 1 centro, 2 direita
    public EscPosBuilder Double(bool on) => Raw(0x1D, 0x21, (byte)(on ? 0x11 : 0x00));
    public EscPosBuilder Separator(char c = '-') => Line(new string(c, Columns));

    /// <summary>Texto à esquerda e valor à direita na mesma linha (corta o texto se precisar).</summary>
    public EscPosBuilder Columns2(string left, string right)
    {
        var space = Columns - right.Length - 1;
        if (space < 1) return Line(left).Line(right.PadLeft(Columns));
        var l = left.Length > space ? left[..space] : left;
        return Line(l.PadRight(space) + " " + right);
    }

    /// <summary>Quebra o texto em linhas da largura do papel.</summary>
    public EscPosBuilder Wrap(string text)
    {
        foreach (var para in text.Split('\n'))
        {
            var line = "";
            foreach (var w in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length + w.Length + (line.Length > 0 ? 1 : 0) > Columns) { Line(line); line = ""; }
                line = line.Length == 0 ? (w.Length > Columns ? w[..Columns] : w) : line + " " + w;
            }
            Line(line);
        }
        return this;
    }

    /// <summary>QR Code nativo (GS ( k) — usado no DANFE da NFC-e.</summary>
    public EscPosBuilder QrCode(string data, byte size = 5)
    {
        var d = Encoding.ASCII.GetBytes(data);
        var len = d.Length + 3;
        Raw(0x1D, 0x28, 0x6B, 4, 0, 0x31, 0x41, 0x32, 0x00);          // modelo 2
        Raw(0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x43, size);                // tamanho do módulo
        Raw(0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x45, 0x31);                // correção de erro M
        Raw(0x1D, 0x28, 0x6B, (byte)(len % 256), (byte)(len / 256), 0x31, 0x50, 0x30).Raw(d); // armazena
        return Raw(0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x51, 0x30);         // imprime
    }

    /// <summary>Pulso na gaveta de dinheiro (conector RJ11 da impressora, pino 2).</summary>
    public EscPosBuilder OpenDrawer() => Raw(0x1B, 0x70, 0x00, 0x19, 0xFA);
    public EscPosBuilder Cut() => Line().Line().Line().Raw(0x1D, 0x56, 0x42, 0x00);
    public byte[] Build() => [.. _bytes];
}

public sealed record PrintLine(string Name, decimal Quantity, long UnitCents, long TotalCents);
public sealed record PrintReceipt(string StoreName, DateTime When, IReadOnlyList<PrintLine> Lines, long TotalCents, long DiscountCents, long PaidCents, long ChangeCents,
    string Method, string? CustomerName = null, int PointsEarned = 0, string? FiscalQrUrl = null, string? FiscalKey = null, string? Footer = null);

/// <summary>Cupom de venda em ESC/POS. Sem NFC-e autorizada é impresso como "não é documento fiscal".</summary>
public static class ReceiptEscPos
{
    private static string M(long c) => (c / 100m).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    public static byte[] Build(PrintReceipt r, int paperMm, bool openDrawer)
    {
        var p = new EscPosBuilder(paperMm);
        if (openDrawer) p.OpenDrawer();
        p.Align(1).Bold(true).Double(true).Line(r.StoreName).Double(false).Bold(false)
         .Line(r.When.ToString("dd/MM/yyyy HH:mm"))
         .Line(r.FiscalQrUrl is null ? "*** NÃO É DOCUMENTO FISCAL ***" : "DANFE NFC-e - Documento Auxiliar")
         .Align(0).Separator();
        foreach (var l in r.Lines)
        {
            p.Line(l.Name.Length > p.Columns ? l.Name[..p.Columns] : l.Name);
            p.Columns2($"  {l.Quantity:0.###} x {M(l.UnitCents)}", M(l.TotalCents));
        }
        p.Separator();
        if (r.DiscountCents > 0) p.Columns2("Desconto", "-" + M(r.DiscountCents));
        p.Bold(true).Columns2("TOTAL R$", M(r.TotalCents)).Bold(false)
         .Columns2(r.Method, M(r.PaidCents));
        if (r.ChangeCents > 0) p.Columns2("Troco", M(r.ChangeCents));
        if (r.CustomerName is not null) p.Line("Cliente: " + r.CustomerName);
        if (r.PointsEarned > 0) p.Line($"Pontos ganhos: {r.PointsEarned}");
        if (r.FiscalQrUrl is not null)
        {
            p.Separator().Align(1).Line("Consulte pela chave de acesso:");
            if (r.FiscalKey is not null) p.Wrap(string.Join(" ", Enumerable.Range(0, (r.FiscalKey.Length + 3) / 4).Select(i => r.FiscalKey.Substring(i * 4, Math.Min(4, r.FiscalKey.Length - i * 4)))));
            p.QrCode(r.FiscalQrUrl).Align(0);
        }
        if (r.Footer is not null) p.Separator().Align(1).Wrap(r.Footer).Align(0);
        return p.Cut().Build();
    }
}

public sealed record NfceDanfeItem(string Code, string Name, decimal Quantity, string Unit, long UnitCents, long TotalCents);
public sealed record NfceDanfePayment(string Name, long Cents);
/// <summary>Dados do DANFE NFC-e devolvidos pelo servidor depois da autorização na SEFAZ.</summary>
public sealed record NfceDanfe(string Status, string Message, int? Number, int? Series, string? AccessKey, string? Protocol, DateTimeOffset? AuthorizedAt,
    DateTimeOffset IssuedAt, bool Homologation, string? QrCode, string? ConsultUrl, string EmitterName, string EmitterCnpj, string? EmitterIe, string EmitterAddress,
    List<NfceDanfeItem> Items, long SubtotalCents, long DiscountCents, long TotalCents, List<NfceDanfePayment> Payments, long ChangeCents)
{
    public bool Printable => Status is "autorizada" && !string.IsNullOrEmpty(AccessKey) && !string.IsNullOrEmpty(QrCode);
    public static string FormatCnpj(string c) => c.Length == 14 ? $"{c[..2]}.{c[2..5]}.{c[5..8]}/{c[8..12]}-{c[12..]}" : c;
    public static string GroupKey(string k) => string.Join(" ", Enumerable.Range(0, (k.Length + 3) / 4).Select(i => k.Substring(i * 4, Math.Min(4, k.Length - i * 4))));
    public string Local(DateTimeOffset d) => d.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
}

/// <summary>
/// DANFE NFC-e em ESC/POS, na ordem do Manual de Padrões Técnicos do DANFE NFC-e: emitente, itens, totais, pagamento,
/// consulta pela chave, consumidor, identificação/protocolo e QR Code.
/// </summary>
public static class DanfeEscPos
{
    private static string M(long c) => (c / 100m).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    public static byte[] Build(NfceDanfe d, int paperMm, bool openDrawer)
    {
        var p = new EscPosBuilder(paperMm);
        if (openDrawer) p.OpenDrawer();
        p.Align(1).Bold(true).Wrap(d.EmitterName).Bold(false)
         .Line($"CNPJ {NfceDanfe.FormatCnpj(d.EmitterCnpj)}" + (d.EmitterIe is { Length: > 0 } ie ? $"  IE {ie}" : ""))
         .Wrap(d.EmitterAddress)
         .Line("Documento Auxiliar da Nota Fiscal").Line("de Consumidor Eletrônica").Align(0).Separator();
        p.Columns2("Código Descrição", "Vl Total");
        foreach (var i in d.Items)
        {
            var name = $"{i.Code} {i.Name}";
            p.Line(name.Length > p.Columns ? name[..p.Columns] : name);
            p.Columns2($"  {i.Quantity:0.###} {i.Unit} x {M(i.UnitCents)}", M(i.TotalCents));
        }
        p.Separator();
        if (d.Homologation) p.Align(1).Bold(true).Wrap("EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO - SEM VALOR FISCAL").Bold(false).Align(0).Separator();
        p.Columns2("Qtde. total de itens", d.Items.Count.ToString())
         .Columns2("Valor total R$", M(d.SubtotalCents));
        if (d.DiscountCents > 0) p.Columns2("Desconto R$", "-" + M(d.DiscountCents));
        p.Bold(true).Columns2("Valor a Pagar R$", M(d.TotalCents)).Bold(false)
         .Columns2("FORMA PAGAMENTO", "VALOR PAGO R$");
        foreach (var pay in d.Payments) p.Columns2(pay.Name, M(pay.Cents));
        if (d.ChangeCents > 0) p.Columns2("Troco R$", M(d.ChangeCents));
        p.Separator().Align(1).Bold(true).Line("Consulte pela Chave de Acesso em").Bold(false)
         .Wrap(d.ConsultUrl ?? "portal da SEFAZ do seu estado")
         .Line(NfceDanfe.GroupKey((d.AccessKey ?? "").PadRight(44)[..24]).Trim())
         .Line(NfceDanfe.GroupKey((d.AccessKey ?? "").PadRight(44)[24..]).Trim())
         .Separator()
         .Bold(true).Line("CONSUMIDOR NÃO IDENTIFICADO").Bold(false)
         .Separator()
         .Bold(true).Line($"NFC-e nº {d.Number:000000000} Série {d.Series:000}").Bold(false)
         .Line(d.Local(d.IssuedAt))
         .Line("Protocolo de autorização: " + d.Protocol);
        if (d.AuthorizedAt is { } at) p.Line("Data de autorização: " + d.Local(at));
        p.QrCode(d.QrCode!, (byte)(paperMm >= 80 ? 5 : 4)).Align(0);
        return p.Cut().Build();
    }
}
