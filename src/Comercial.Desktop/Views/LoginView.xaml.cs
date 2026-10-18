using System.Windows;
using System.Windows.Controls;

namespace Comercial.Desktop.Views;

public partial class LoginView : UserControl
{
    private bool _syncing;

    public LoginView() => InitializeComponent();

    // PasswordBox não aceita binding; o campo visível ("olho") é mantido sincronizado manualmente.
    private void Reveal_Changed(object sender, RoutedEventArgs e)
    {
        var show = Reveal.IsChecked == true;
        _syncing = true;
        if (show) PwdVisible.Text = Pwd.Password;
        _syncing = false;
        PwdVisible.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Pwd.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        (show ? (Control)PwdVisible : Pwd).Focus();
    }

    private void PwdVisible_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Pwd.Password = PwdVisible.Text;
        _syncing = false;
    }

    private void Pwd_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || Reveal.IsChecked != true) return;
        _syncing = true;
        PwdVisible.Text = Pwd.Password;
        _syncing = false;
    }
}
