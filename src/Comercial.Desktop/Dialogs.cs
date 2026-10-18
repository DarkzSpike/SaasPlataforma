using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

/// <summary>
/// Janela de venda concluída: mostra o troco e só fecha quando o operador confirma que entregou.
/// Enquanto aberta, nenhuma nova venda pode ser registrada.
/// </summary>
public sealed class SaleDoneViewModel(long totalCents, long paidCents, string method, Action onConfirm) : ObservableObject
{
    public long ChangeCents => paidCents - totalCents;
    public bool HasChange => ChangeCents > 0;
    public string ChangeText => AppServices.Money(ChangeCents);
    public string TotalText => AppServices.Money(totalCents);
    public string PaidText => AppServices.Money(paidCents);
    public string Method => method;
    public string Headline => HasChange ? "Entregue o troco ao cliente" : "Venda registrada";
    public string ConfirmText => HasChange ? "Troco entregue — próxima venda" : "Próxima venda";
    public RelayCommand ConfirmCommand { get; } = RelayCommand.Sync(_ => onConfirm());
}

public sealed class FormField(string key, string label, string value = "", string icon = "", string placeholder = "") : ObservableObject
{
    private string _value = value;
    public string Key { get; } = key;
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public string Placeholder { get; } = placeholder;
    public string Value { get => _value; set => Set(ref _value, value); }
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

    public static BitmapImage? Load(byte[] bytes)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 320;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception) { return null; }
    }

    public static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() == ".png" ? "image/png" : "image/jpeg";
}
