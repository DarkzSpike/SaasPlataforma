using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

/// <summary>QR Code do PIX no caixa: consulta o servidor (que consulta o Mercado Pago) até aprovar, cancelar ou expirar.</summary>
public sealed class PixPayViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly Action<Guid> _onApproved;
    private readonly Action<string> _onClosed;
    private string _status = "Aguardando o pagamento...";
    public Guid ChargeId { get; }
    public string AmountText { get; }
    public string QrCode { get; }
    public BitmapSource? QrImage { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CopyCommand { get; }

    public PixPayViewModel(AppServices s, JsonElement charge, Action<Guid> onApproved, Action<string> onClosed)
    {
        _s = s; _onApproved = onApproved; _onClosed = onClosed;
        ChargeId = charge.GetProperty("id").GetGuid();
        AmountText = AppServices.Money(charge.GetProperty("amountCents").GetInt64());
        QrCode = charge.GetProperty("qrCode").GetString() ?? "";
        ExpiresAt = charge.GetProperty("expiresAt").GetDateTimeOffset();
        QrImage = Decode(charge.GetProperty("qrBase64").GetString());
        CancelCommand = new RelayCommand(async _ => await CancelAsync());
        CopyCommand = RelayCommand.Sync(_ => { try { System.Windows.Clipboard.SetText(QrCode); Status = "Código copiado."; } catch { } });
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    private static BitmapSource? Decode(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = new MemoryStream(Convert.FromBase64String(b64)); img.EndInit(); img.Freeze();
            return img;
        }
        catch (Exception) { return null; }
    }

    private bool _busy;
    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var r = await _s.Api.GetAsync($"/api/v1/pos/pix-charges/{ChargeId}");
            switch (r.GetProperty("status").GetString())
            {
                case "approved": _timer.Stop(); Status = "Pagamento confirmado!"; _onApproved(ChargeId); break;
                case "cancelled": _timer.Stop(); _onClosed("PIX cancelado ou recusado."); break;
                case "expired": _timer.Stop(); _onClosed("O QR Code expirou. Gere outro ou escolha outra forma de pagamento."); break;
                default:
                    var left = ExpiresAt - DateTimeOffset.Now;
                    Status = left > TimeSpan.Zero ? $"Aguardando o pagamento... expira em {left:mm\\:ss}" : "Verificando...";
                    break;
            }
        }
        catch (ApiException) { Status = "Erro ao consultar o pagamento; tentando de novo..."; }
        catch (HttpRequestException) { Status = "Sem conexão; tentando de novo..."; }
        finally { _busy = false; }
    }

    private async Task CancelAsync()
    {
        _timer.Stop();
        try { await _s.Api.PostAsync($"/api/v1/pos/pix-charges/{ChargeId}/cancel", new { }); } catch (Exception ex) when (ex is ApiException or HttpRequestException) { }
        // Corrida: o cliente pode ter pago no último segundo — confere antes de desistir.
        try
        {
            var r = await _s.Api.GetAsync($"/api/v1/pos/pix-charges/{ChargeId}");
            if (r.GetProperty("status").GetString() == "approved") { _onApproved(ChargeId); return; }
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { }
        _onClosed("PIX cancelado. A venda continua aberta.");
    }

    public void Stop() => _timer.Stop();
}
