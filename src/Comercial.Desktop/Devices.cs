using System.IO.Ports;
using System.Printing;
using System.Runtime.InteropServices;
using Comercial.Desktop.Core.Hardware;

namespace Comercial.Desktop;

/// <summary>Configuração dos periféricos DESTE terminal (fica no computador, não no servidor).</summary>
public sealed class DeviceSettings
{
    public string? PrinterName { get; set; }
    public int PaperMm { get; set; } = 80;
    public bool EscPos { get; set; }
    public bool AutoPrint { get; set; }
    public bool OpenDrawerOnCash { get; set; }
    public string? LabelPrinterName { get; set; }
    public string? ScalePort { get; set; }
    public int ScaleBaud { get; set; } = 9600;
    public ScaleBarcodeMode ScaleBarcodeMode { get; set; } = ScaleBarcodeMode.Price;
    public int ScaleCodeDigits { get; set; } = 4;
    public string? ReceiptFooter { get; set; }
}

/// <summary>Envia bytes "crus" (ESC/POS, ZPL) para uma impressora do Windows pelo spooler, sem driver gráfico.</summary>
public static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo { public string? Name; public string? Output; public string? DataType; }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinter(string name, out IntPtr h, IntPtr d);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr h);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDocPrinter(IntPtr h, int level, [In] DocInfo di);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(IntPtr h);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr h, byte[] bytes, int count, out int written);

    public static void Send(string printer, byte[] data, string docName = "Cupom")
    {
        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) throw new InvalidOperationException($"Impressora \"{printer}\" não encontrada.");
        try
        {
            if (StartDocPrinter(h, 1, new DocInfo { Name = docName, DataType = "RAW" }) == 0) throw new InvalidOperationException("A impressora recusou o trabalho.");
            try
            {
                StartPagePrinter(h);
                if (!WritePrinter(h, data, data.Length, out var written) || written != data.Length) throw new InvalidOperationException("Falha ao enviar dados para a impressora.");
                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }

    public static IReadOnlyList<string> Installed()
    {
        try { using var server = new LocalPrintServer(); return server.GetPrintQueues().Select(q => q.FullName).OrderBy(n => n).ToList(); }
        catch (Exception) { return []; }
    }
}

/// <summary>Lê o peso de balança serial (protocolo ENQ/STX...ETX).</summary>
public static class SerialScale
{
    public static IReadOnlyList<string> Ports() => SerialPort.GetPortNames().OrderBy(p => p).ToList();

    public static SerialScaleProtocol.Reading Read(string port, int baud)
    {
        try
        {
            using var sp = new SerialPort(port, baud, Parity.None, 8, StopBits.One) { ReadTimeout = 1500, WriteTimeout = 1000 };
            sp.Open();
            sp.DiscardInBuffer();
            sp.Write([SerialScaleProtocol.Enq], 0, 1);
            var buf = new List<byte>();
            var deadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < deadline)
            {
                var b = sp.ReadByte();
                if (b < 0) break;
                buf.Add((byte)b);
                if (b == SerialScaleProtocol.Etx) break;
            }
            return SerialScaleProtocol.Parse([.. buf]);
        }
        catch (TimeoutException) { return new(false, 0, "A balança não respondeu. Confira o cabo e a porta."); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or ArgumentException) { return new(false, 0, "Porta da balança indisponível: " + ex.Message); }
    }
}
