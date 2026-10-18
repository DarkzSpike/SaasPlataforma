using System.Windows;
using System.Windows.Input;

namespace Comercial.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        // F10 finaliza a venda no PDV.
        InputBindings.Add(new KeyBinding(new RelayCommand(_ =>
        {
            if (((MainViewModel)DataContext).Current is PosViewModel pos && pos.FinishCommand.CanExecute(null)) pos.FinishCommand.Execute(null);
            return Task.CompletedTask;
        }), Key.F10, ModifierKeys.None));
    }
}
