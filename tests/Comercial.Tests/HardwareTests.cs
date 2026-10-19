using System.Text;
using Comercial.Desktop.Core.Hardware;

namespace Comercial.Tests;

/// <summary>Protocolos dos periféricos (o envio físico depende do equipamento e não é testável aqui).</summary>
public class HardwareTests
{
    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Fact]
    public void Cupom_escpos_tem_inicializacao_acentos_gaveta_qrcode_e_corte()
    {
        var bytes = ReceiptEscPos.Build(new PrintReceipt("Loja Ação", new DateTime(2026, 10, 19, 10, 0, 0),
            [new PrintLine("Pão de queijo", 2, 350, 700)], 700, 0, 1000, 300, "Dinheiro", FiscalQrUrl: "https://nfce.fazenda.sp.gov.br/qrcode?p=123", FiscalKey: "35261011222333000181650010000000011000000019"), 58, openDrawer: true);
        Assert.Equal([0x1B, 0x40], bytes[..2]);                                     // ESC @
        Assert.True(IndexOf(bytes, [0x1B, 0x74, 3]) > 0);                            // página 860
        Assert.True(IndexOf(bytes, [0x1B, 0x70, 0x00, 0x19, 0xFA]) > 0);             // pulso da gaveta
        Assert.True(IndexOf(bytes, [0x1D, 0x28, 0x6B]) > 0);                         // QR Code
        Assert.True(IndexOf(bytes, [0x1D, 0x56, 0x42, 0x00]) > 0);                   // corte
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.True(IndexOf(bytes, Encoding.GetEncoding(860).GetBytes("Pão de queijo")) > 0); // acentos em CP860
        var text = Encoding.GetEncoding(860).GetString(bytes);
        Assert.Contains("Troco", text);
        Assert.DoesNotContain("NÃO É DOCUMENTO FISCAL", text);                        // com NFC-e vira DANFE
        Assert.All(text.Split('\n').Where(l => !l.Contains('\x1B') && !l.Contains('\x1D')), l => Assert.True(l.Length <= 32, l)); // cabe em 58 mm
    }

    [Fact]
    public void Cupom_sem_nfce_avisa_que_nao_e_fiscal()
    {
        var bytes = ReceiptEscPos.Build(new PrintReceipt("Loja", DateTime.Now, [new PrintLine("Item", 1, 100, 100)], 100, 0, 100, 0, "PIX"), 80, openDrawer: false);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Contains("NÃO É DOCUMENTO FISCAL", Encoding.GetEncoding(860).GetString(bytes));
        Assert.Equal(-1, IndexOf(bytes, [0x1B, 0x70, 0x00]));                         // sem gaveta
    }

    private static string Ean(string twelve)
    {
        var sum = twelve.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return twelve + ((10 - sum % 10) % 10);
    }

    [Fact]
    public void Etiqueta_de_balanca_com_preco_ou_peso()
    {
        Assert.True(ScaleBarcodeParser.TryParse(Ean("200010001234"), new(ScaleBarcodeMode.Price, 4, 7), out var a));
        Assert.Equal("0001", a.ProductCode); Assert.Equal(1234L, a.PriceCents);              // R$ 12,34
        Assert.True(ScaleBarcodeParser.TryParse(Ean("200002000750"), new(ScaleBarcodeMode.Weight, 5, 6), out var b));
        Assert.Equal("00002", b.ProductCode); Assert.Equal(750, b.WeightGrams);               // 0,750 kg
        Assert.False(ScaleBarcodeParser.TryParse(Ean("200010001234"), new(ScaleBarcodeMode.Price, 4, 6), out _)); // configuração que não soma 11
    }

    [Fact]
    public void Etiqueta_de_balanca_recusa_codigo_comum_ou_digito_errado()
    {
        Assert.False(ScaleBarcodeParser.TryParse("7891000100011", ScaleBarcodeConfig.Default, out _)); // EAN comum
        Assert.False(ScaleBarcodeParser.TryParse(Ean("200010001234")[..12] + "9" == Ean("200010001234") ? Ean("200010001234")[..12] + "8" : Ean("200010001234")[..12] + "9", new(ScaleBarcodeMode.Price, 4, 7), out _)); // dígito verificador errado
        Assert.True(ScaleBarcodeParser.IsEan13Valid("7891000100011"));
    }

    [Fact]
    public void Protocolo_da_balanca_serial()
    {
        static byte[] F(string s) => [SerialScaleProtocol.Stx, .. Encoding.ASCII.GetBytes(s), SerialScaleProtocol.Etx];
        var r = SerialScaleProtocol.Parse(F("01250"));
        Assert.True(r.Ok); Assert.Equal(1.250m, r.Kilograms);
        Assert.Equal("Peso instável: aguarde estabilizar.", SerialScaleProtocol.Parse(F("IIIII")).Error);
        Assert.False(SerialScaleProtocol.Parse(F("SSSSS")).Ok);
        Assert.False(SerialScaleProtocol.Parse(F("00000")).Ok);
        Assert.False(SerialScaleProtocol.Parse(Encoding.ASCII.GetBytes("0125")).Ok); // sem STX/ETX
    }

    [Fact]
    public void Etiqueta_zpl_sem_acentos_com_preco_e_codigo_de_barras()
    {
        var zpl = LabelZpl.Build([new LabelItem("Pão francês ^ teste", 1990, "7891000100011", "KG")], copies: 3);
        Assert.StartsWith("^XA", zpl);
        Assert.Contains("Pao frances   teste", zpl);       // acentos e ^ removidos
        Assert.Contains("R$ 19,90", zpl);
        Assert.Contains("^BEN", zpl);                       // EAN-13
        Assert.Contains("/kg", zpl);
        Assert.Contains("^PQ3", zpl);
    }
}
