using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

/// <summary>
/// Janela de venda concluída: mostra o troco e só fecha quando o operador confirma que entregou.
/// Enquanto aberta, nenhuma nova venda pode ser registrada.
/// </summary>
public sealed class SaleDoneViewModel(long totalCents, long paidCents, string method, Action onConfirm, Receipt? receipt = null) : ObservableObject
{
    public Receipt? Receipt { get; } = receipt;
    public bool CanPrint => Receipt is not null;
    public RelayCommand PrintCommand { get; } = RelayCommand.Sync(_ => { if (receipt is { } r) ReceiptPrinter.Print(r); });
    public string PointsText => Receipt is { PointsEarned: > 0 } r ? $"Cliente ganhou {r.PointsEarned} ponto(s)" : "";
    public long ChangeCents => paidCents - totalCents;
    public bool HasChange => ChangeCents > 0;
    public string ChangeText => AppServices.Money(ChangeCents);
    public string TotalText => AppServices.Money(totalCents);
    public string PaidText => AppServices.Money(paidCents);
    public string Method => method;
    public string Headline => HasChange ? "Entregue o troco ao cliente" : "Venda registrada";
    public string ConfirmText => HasChange ? "Troco entregue — próxima venda" : "Próxima venda";
    public RelayCommand ConfirmCommand { get; } = RelayCommand.Sync(_ => onConfirm());

    // NFC-e (preenchido quando a SEFAZ responde, logo depois da venda)
    private Comercial.Desktop.Core.Hardware.NfceDanfe? _danfe;
    private string _fiscalText = "";
    public string FiscalText { get => _fiscalText; set { if (Set(ref _fiscalText, value)) Raise(nameof(HasFiscalText)); } }
    public bool HasFiscalText => FiscalText.Length > 0;
    public bool CanPrintDanfe => _danfe is { Printable: true };
    public void SetDanfe(Comercial.Desktop.Core.Hardware.NfceDanfe d) { _danfe = d; Raise(nameof(CanPrintDanfe)); }
    public RelayCommand PrintDanfeCommand => RelayCommand.Sync(_ => { if (_danfe is { Printable: true } d) ReceiptPrinter.PrintDanfe(d); });
}

public sealed class FormField(string key, string label, string value = "", string icon = "", string placeholder = "", IReadOnlyList<string>? options = null) : ObservableObject
{
    private string _value = value;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public string Placeholder { get; } = placeholder;
    /// <summary>Quando informado, o campo vira uma lista de escolha.</summary>
    public IReadOnlyList<string>? Options { get; } = options;
    public bool IsChoice => Options is not null;
    public bool IsText => Options is null;
    public string Value { get => _value; set => Set(ref _value, value); }

    // Campo calculado (ex.: preço de venda a partir do custo e da % de lucro) e observação abaixo do campo.
    private bool _readOnly;
    private string _note = "";
    public bool IsReadOnly { get => _readOnly; set => Set(ref _readOnly, value); }
    public string Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => Note.Length > 0;
}

/// <summary>Formulário sobreposto (campos de texto + foto opcional), usado no cadastro de produto e na entrada de estoque.</summary>
public sealed class FormViewModel : ObservableObject
{
    private string _error = "";
    private string? _imagePath;
    private BitmapImage? _preview;
    private bool _busy;

    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string ConfirmText { get; init; } = "Salvar";
    public bool AllowImage { get; init; }
    public ObservableCollection<FormField> Fields { get; } = [];
    public string Error { get => _error; set => Set(ref _error, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }
    public string? ImagePath { get => _imagePath; private set { Set(ref _imagePath, value); Raise(nameof(HasImage)); } }
    public bool HasImage => Preview is not null;
    public BitmapImage? Preview { get => _preview; set { Set(ref _preview, value); Raise(nameof(HasImage)); } }
    public RelayCommand? ConfirmCommand { get; set; }
    public RelayCommand? CancelCommand { get; set; }
    public RelayCommand PickImageCommand { get; }

    public FormViewModel()
    {
        PickImageCommand = RelayCommand.Sync(_ =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Escolha a foto do produto", Filter = "Fotos (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png" };
            if (dlg.ShowDialog() != true) return;
            if (new FileInfo(dlg.FileName).Length > 5 * 1024 * 1024) { Error = "A foto deve ter até 5 MB."; return; }
            ImagePath = dlg.FileName;
            Preview = Load(File.ReadAllBytes(dlg.FileName));
        });
    }

    public string this[string key] => Fields.First(f => f.Key == key).Value.Trim();

    /// <param name="decodeWidth">Largura para decodificar (miniaturas: 320; 0 = tamanho original).</param>
    public static BitmapImage? Load(byte[] bytes, int decodeWidth = 320)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception) { return null; }
    }

    public static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
}
