using System.Collections.ObjectModel;
using System.Text;
using Comercial.Desktop.Core.Hardware;

namespace Comercial.Desktop;

/// <summary>Periféricos deste terminal: impressora térmica, gaveta, balança e etiquetas. Fica salvo só neste computador.</summary>
public sealed class DevicesViewModel(ShellViewModel shell) : OwnerPage(shell, "Dispositivos", "Impressora, gaveta de dinheiro, balança e etiquetas deste computador")
{
    private DeviceSettings D => S.Settings.Devices;
    public ObservableCollection<string> Printers { get; } = [];
    public ObservableCollection<string> Ports { get; } = [];
    public IReadOnlyList<string> PaperOptions { get; } = ["58 mm", "80 mm"];
    public IReadOnlyList<string> BarcodeModes { get; } = ["Preço na etiqueta", "Peso na etiqueta"];
    public IReadOnlyList<string> CodeDigitOptions { get; } = ["Código com 4 dígitos (2 CCCC VVVVVVV D)", "Código com 5 dígitos (2 CCCCC VVVVVV D)", "Código com 6 dígitos (2 CCCCCC VVVVV D)"];
    private string _info = "";
    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;

    public string? Printer { get => D.PrinterName; set { D.PrinterName = value; Raise(); } }
    public string Paper { get => D.PaperMm <= 58 ? PaperOptions[0] : PaperOptions[1]; set { D.PaperMm = value == PaperOptions[0] ? 58 : 80; Raise(); } }
    public bool EscPos { get => D.EscPos; set { D.EscPos = value; Raise(); } }
    public bool AutoPrint { get => D.AutoPrint; set { D.AutoPrint = value; Raise(); } }
    public bool OpenDrawerOnCash { get => D.OpenDrawerOnCash; set { D.OpenDrawerOnCash = value; Raise(); } }
    public string? LabelPrinter { get => D.LabelPrinterName; set { D.LabelPrinterName = value; Raise(); } }
    public string? ScalePort { get => D.ScalePort; set { D.ScalePort = value; Raise(); } }
    public string ScaleBaud { get => D.ScaleBaud.ToString(); set { if (int.TryParse(value, out var b) && b is >= 1200 and <= 115200) D.ScaleBaud = b; Raise(); } }
    public string BarcodeMode { get => D.ScaleBarcodeMode == ScaleBarcodeMode.Weight ? BarcodeModes[1] : BarcodeModes[0]; set { D.ScaleBarcodeMode = value == BarcodeModes[1] ? ScaleBarcodeMode.Weight : ScaleBarcodeMode.Price; Raise(); } }
    public string CodeDigits { get => CodeDigitOptions[Math.Clamp(D.ScaleCodeDigits - 4, 0, 2)]; set { D.ScaleCodeDigits = Array.IndexOf(CodeDigitOptions.ToArray(), value) + 4; Raise(); } }
    public string Footer { get => D.ReceiptFooter ?? ""; set { D.ReceiptFooter = value.Length == 0 ? null : value; Raise(); } }

    public RelayCommand SaveCommand => RelayCommand.Sync(_ => { S.Settings.Save(); ReceiptPrinter.Devices = D; Error = ""; Info = "Configuração salva neste computador."; });

    public RelayCommand TestPrintCommand => RelayCommand.Sync(_ => Try(() =>
    {
        if (string.IsNullOrWhiteSpace(D.PrinterName)) throw new InvalidOperationException("Escolha a impressora.");
        var b = new EscPosBuilder(D.PaperMm).Align(1).Bold(true).Line("TESTE DE IMPRESSÃO").Bold(false).Line(DateTime.Now.ToString("dd/MM/yyyy HH:mm"))
            .Line("Acentuação: ação, café, pão, maçã").Separator().Columns2("Item de teste", "1,00").Cut().Build();
        RawPrinter.Send(D.PrinterName!, b, "Teste");
        return "Teste enviado. Se saiu sem acentos ou com caracteres estranhos, a impressora pode não ser ESC/POS (use o modo Windows).";
    }));

    public RelayCommand TestDrawerCommand => RelayCommand.Sync(_ => Try(() =>
    {
        if (!D.EscPos || string.IsNullOrWhiteSpace(D.PrinterName)) throw new InvalidOperationException("A gaveta abre pela impressora térmica: escolha a impressora e ative ESC/POS.");
        RawPrinter.Send(D.PrinterName!, new EscPosBuilder(D.PaperMm).OpenDrawer().Build(), "Gaveta");
        return "Pulso enviado para a gaveta.";
    }));

    public RelayCommand TestScaleCommand => RelayCommand.Sync(_ => Try(() =>
    {
        if (string.IsNullOrWhiteSpace(D.ScalePort)) throw new InvalidOperationException("Escolha a porta da balança.");
        var r = SerialScale.Read(D.ScalePort!, D.ScaleBaud);
        return r.Ok ? $"Peso lido: {r.Kilograms:0.000} kg" : "Balança: " + r.Error;
    }));

    public RelayCommand TestLabelCommand => RelayCommand.Sync(_ => Try(() =>
    {
        if (string.IsNullOrWhiteSpace(D.LabelPrinterName)) throw new InvalidOperationException("Escolha a impressora de etiquetas.");
        RawPrinter.Send(D.LabelPrinterName!, Encoding.UTF8.GetBytes(LabelZpl.Build([new LabelItem("Etiqueta de teste", 1990, "7891000100011")])), "Etiqueta");
        return "Etiqueta de teste enviada (ZPL).";
    }));

    private void Try(Func<string> action)
    {
        Error = ""; Info = "";
        try { Info = action(); }
        catch (InvalidOperationException ex) { Error = ex.Message; }
    }

    protected override Task LoadCoreAsync()
    {
        Printers.Clear(); foreach (var p in RawPrinter.Installed()) Printers.Add(p);
        Ports.Clear(); foreach (var p in SerialScale.Ports()) Ports.Add(p);
        foreach (var n in new[] { nameof(Printer), nameof(LabelPrinter), nameof(ScalePort) }) Raise(n);
        return Task.CompletedTask;
    }
}
