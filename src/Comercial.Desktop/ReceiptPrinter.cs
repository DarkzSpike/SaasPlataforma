using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Comercial.Desktop;

public sealed record ReceiptLine(string Name, decimal Quantity, long UnitCents, long TotalCents);

public sealed record Receipt(DateTime When, IReadOnlyList<ReceiptLine> Lines, long TotalCents, long DiscountCents, long PaidCents, string Method,
    string? CustomerName = null, int PointsEarned = 0, string? Reference = null);

/// <summary>
/// Comprovante NÃO FISCAL na impressora do Windows (térmica instalada como impressora comum ou qualquer outra).
/// O cupom fiscal (NFC-e) depende da integração fiscal.
/// </summary>
public static class ReceiptPrinter
{
    /// <summary>Periféricos do terminal (definidos ao abrir o PDV e ao salvar em Dispositivos).</summary>
    public static DeviceSettings Devices { get; set; } = new();
    private static bool UseEscPos => Devices.EscPos && !string.IsNullOrWhiteSpace(Devices.PrinterName);

    /// <summary>Abre a gaveta de dinheiro (pulso pela impressora térmica). Silencioso se não houver impressora configurada.</summary>
    public static string? OpenDrawer()
    {
        if (!UseEscPos) return null;
        try { RawPrinter.Send(Devices.PrinterName!, new Comercial.Desktop.Core.Hardware.EscPosBuilder(Devices.PaperMm).OpenDrawer().Build(), "Gaveta"); return null; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    private static byte[] EscPosBytes(Receipt r, bool drawer) => Comercial.Desktop.Core.Hardware.ReceiptEscPos.Build(new(
        Application.Current?.Resources["SystemName"] as string ?? "Loja", r.When,
        r.Lines.Select(l => new Comercial.Desktop.Core.Hardware.PrintLine(l.Name, l.Quantity, l.UnitCents, l.TotalCents)).ToList(),
        r.TotalCents, r.DiscountCents, r.PaidCents, Math.Max(0, r.PaidCents - r.TotalCents), r.Method, r.CustomerName, r.PointsEarned,
        Footer: Devices.ReceiptFooter ?? "Obrigado pela preferência!"), Devices.PaperMm, drawer);

    /// <summary>Impressão automática ao finalizar a venda (térmica ESC/POS). Devolve mensagem de erro ou null.</summary>
    public static string? AutoPrint(Receipt r, bool cashSale)
    {
        if (!UseEscPos) return null;
        try
        {
            if (Devices.AutoPrint) RawPrinter.Send(Devices.PrinterName!, EscPosBytes(r, cashSale && Devices.OpenDrawerOnCash), "Cupom");
            else if (cashSale && Devices.OpenDrawerOnCash) return OpenDrawer();
            return null;
        }
        catch (InvalidOperationException ex) { return "Impressora: " + ex.Message; }
    }
    public static FlowDocument Build(Receipt r)
    {
        var res = Application.Current.Resources;
        var doc = new FlowDocument { PageWidth = 300, PagePadding = new Thickness(12), FontFamily = new FontFamily("Consolas, Courier New"), FontSize = 11, ColumnWidth = 300 };
        Paragraph P(string text, bool bold = false, TextAlignment align = TextAlignment.Left, double size = 11) =>
            new(new Run(text)) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextAlignment = align, FontSize = size, Margin = new Thickness(0, 1, 0, 1) };
        string Money(long c) => AppServices.Money(c);
        doc.Blocks.Add(P(res["SystemName"] as string ?? "", true, TextAlignment.Center, 14));
        doc.Blocks.Add(P(r.When.ToString("dd/MM/yyyy HH:mm") + (r.Reference is null ? "" : "  •  " + r.Reference), align: TextAlignment.Center));
        doc.Blocks.Add(P(new string('-', 40)));
        foreach (var l in r.Lines)
        {
            doc.Blocks.Add(P(l.Name, true));
            doc.Blocks.Add(P($"{l.Quantity:0.###} x {Money(l.UnitCents)}".PadRight(26) + Money(l.TotalCents).PadLeft(14)));
        }
        doc.Blocks.Add(P(new string('-', 40)));
        if (r.DiscountCents > 0) doc.Blocks.Add(P("Descontos".PadRight(26) + ("-" + Money(r.DiscountCents)).PadLeft(14)));
        doc.Blocks.Add(P("TOTAL".PadRight(26) + Money(r.TotalCents).PadLeft(14), true, size: 13));
        doc.Blocks.Add(P(r.Method.PadRight(26) + Money(r.PaidCents).PadLeft(14)));
        if (r.PaidCents > r.TotalCents) doc.Blocks.Add(P("Troco".PadRight(26) + Money(r.PaidCents - r.TotalCents).PadLeft(14), true));
        if (r.CustomerName is not null) doc.Blocks.Add(P("Cliente: " + r.CustomerName));
        if (r.PointsEarned > 0) doc.Blocks.Add(P($"Pontos ganhos: {r.PointsEarned}"));
        doc.Blocks.Add(P(new string('-', 40)));
        doc.Blocks.Add(P("NÃO É DOCUMENTO FISCAL", true, TextAlignment.Center));
        doc.Blocks.Add(P("Obrigado pela preferência!", align: TextAlignment.Center));
        return doc;
    }

    /// <summary>DANFE NFC-e na impressora do Windows (mesma ordem do layout oficial), com o QR Code como imagem.</summary>
    public static FlowDocument BuildDanfe(Comercial.Desktop.Core.Hardware.NfceDanfe d)
    {
        var doc = new FlowDocument { PageWidth = 300, PagePadding = new Thickness(10), FontFamily = new FontFamily("Consolas, Courier New"), FontSize = 10, ColumnWidth = 300 };
        Paragraph P(string text, bool bold = false, TextAlignment align = TextAlignment.Left, double size = 10) =>
            new(new Run(text)) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, TextAlignment = align, FontSize = size, Margin = new Thickness(0, 1, 0, 1) };
        string Money(long c) => (c / 100m).ToString("N2", AppServices.PtBr);
        string Cols(string l, string r) => l.PadRight(Math.Max(1, 44 - r.Length)) + r;
        var line = new string('-', 44);
        doc.Blocks.Add(P(d.EmitterName, true, TextAlignment.Center, 11));
        doc.Blocks.Add(P($"CNPJ {Comercial.Desktop.Core.Hardware.NfceDanfe.FormatCnpj(d.EmitterCnpj)}" + (d.EmitterIe is { Length: > 0 } ie ? $"  IE {ie}" : ""), align: TextAlignment.Center));
        doc.Blocks.Add(P(d.EmitterAddress, align: TextAlignment.Center));
        doc.Blocks.Add(P("Documento Auxiliar da Nota Fiscal de Consumidor Eletrônica", true, TextAlignment.Center));
        doc.Blocks.Add(P(line));
        foreach (var i in d.Items)
        {
            doc.Blocks.Add(P($"{i.Code} {i.Name}"));
            doc.Blocks.Add(P(Cols($"  {i.Quantity:0.###} {i.Unit} x {Money(i.UnitCents)}", Money(i.TotalCents))));
        }
        doc.Blocks.Add(P(line));
        if (d.Homologation) doc.Blocks.Add(P("EMITIDA EM AMBIENTE DE HOMOLOGAÇÃO - SEM VALOR FISCAL", true, TextAlignment.Center));
        doc.Blocks.Add(P(Cols("Qtde. total de itens", d.Items.Count.ToString())));
        doc.Blocks.Add(P(Cols("Valor total R$", Money(d.SubtotalCents))));
        if (d.DiscountCents > 0) doc.Blocks.Add(P(Cols("Desconto R$", "-" + Money(d.DiscountCents))));
        doc.Blocks.Add(P(Cols("Valor a Pagar R$", Money(d.TotalCents)), true, size: 11));
        doc.Blocks.Add(P(Cols("FORMA PAGAMENTO", "VALOR PAGO R$")));
        foreach (var p in d.Payments) doc.Blocks.Add(P(Cols(p.Name, Money(p.Cents))));
        if (d.ChangeCents > 0) doc.Blocks.Add(P(Cols("Troco R$", Money(d.ChangeCents))));
        doc.Blocks.Add(P(line));
        doc.Blocks.Add(P("Consulte pela Chave de Acesso em", true, TextAlignment.Center));
        doc.Blocks.Add(P(d.ConsultUrl ?? "portal da SEFAZ do seu estado", align: TextAlignment.Center));
        var key = (d.AccessKey ?? "").PadRight(44);
        doc.Blocks.Add(P(Comercial.Desktop.Core.Hardware.NfceDanfe.GroupKey(key[..24]).Trim(), align: TextAlignment.Center));
        doc.Blocks.Add(P(Comercial.Desktop.Core.Hardware.NfceDanfe.GroupKey(key[24..]).Trim(), align: TextAlignment.Center));
        doc.Blocks.Add(P("CONSUMIDOR NÃO IDENTIFICADO", true, TextAlignment.Center));
        doc.Blocks.Add(P($"NFC-e nº {d.Number:000000000} Série {d.Series:000} {d.Local(d.IssuedAt)}", true, TextAlignment.Center));
        doc.Blocks.Add(P("Protocolo de autorização: " + d.Protocol, align: TextAlignment.Center));
        if (d.AuthorizedAt is { } at) doc.Blocks.Add(P("Data de autorização: " + d.Local(at), align: TextAlignment.Center));
        using (var gen = new QRCoder.QRCodeGenerator())
        using (var data = gen.CreateQrCode(d.QrCode ?? "", QRCoder.QRCodeGenerator.ECCLevel.M))
        {
            var img = new Image { Source = FormViewModel.Load(new QRCoder.PngByteQRCode(data).GetGraphic(6), 0), Width = 150, Height = 150 };
            doc.Blocks.Add(new BlockUIContainer(img) { Margin = new Thickness(0, 6, 0, 0) });
        }
        return doc;
    }

    /// <summary>Impressão do DANFE ao finalizar (térmica ESC/POS, se configurada). Devolve mensagem de erro ou null.</summary>
    public static string? AutoPrintDanfe(Comercial.Desktop.Core.Hardware.NfceDanfe d)
    {
        if (!UseEscPos || !Devices.AutoPrint) return null;
        try { RawPrinter.Send(Devices.PrinterName!, Comercial.Desktop.Core.Hardware.DanfeEscPos.Build(d, Devices.PaperMm, false), "NFC-e"); return null; }
        catch (InvalidOperationException ex) { return "Impressora: " + ex.Message; }
    }

    public static void PrintDanfe(Comercial.Desktop.Core.Hardware.NfceDanfe d)
    {
        if (UseEscPos)
        {
            try { RawPrinter.Send(Devices.PrinterName!, Comercial.Desktop.Core.Hardware.DanfeEscPos.Build(d, Devices.PaperMm, false), "NFC-e"); return; }
            catch (InvalidOperationException ex) { MessageBox.Show(ex.Message, "Impressora", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        var doc = BuildDanfe(d);
        doc.PageWidth = Math.Min(dlg.PrintableAreaWidth, 300);
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "DANFE NFC-e");
    }

    public static void Print(Receipt r)
    {
        if (UseEscPos)
        {
            try { RawPrinter.Send(Devices.PrinterName!, EscPosBytes(r, false), "Cupom"); return; }
            catch (InvalidOperationException ex) { MessageBox.Show(ex.Message, "Impressora", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        var doc = Build(r);
        doc.PageWidth = Math.Min(dlg.PrintableAreaWidth, 300);
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "Comprovante de venda");
    }
}
