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
