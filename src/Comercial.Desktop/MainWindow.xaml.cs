using System.Windows;
using System.Windows.Input;

namespace Comercial.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel? vm = null)
    {
        InitializeComponent();
        DataContext = vm ?? new MainViewModel();
        // F10 finaliza a venda no PDV.
        InputBindings.Add(new KeyBinding(RelayCommand.Sync(_ =>
        {
            if (((MainViewModel)DataContext).Current is ShellViewModel { Page: PosViewModel pos } && pos.FinishCommand.CanExecute(null))
                pos.FinishCommand.Execute(null);
        }), Key.F10, ModifierKeys.None));
        // F1 abre a ajuda.
        InputBindings.Add(new KeyBinding(RelayCommand.Sync(_ =>
        {
            if (((MainViewModel)DataContext).Current is ShellViewModel shell && shell.Nav.FirstOrDefault(n => n.Key == "help") is { } help) shell.SelectedNav = help;
        }), Key.F1, ModifierKeys.None));
    }
}
