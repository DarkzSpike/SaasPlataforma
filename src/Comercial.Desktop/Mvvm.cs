using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Comercial.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([System.Runtime.CompilerServices.CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Func<object?, Task> run, Func<object?, bool>? can = null) : ICommand
{
    private bool _busy;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => !_busy && (can?.Invoke(p) ?? true);

    public async void Execute(object? p)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try { await run(p); }
        finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
    }

    public static RelayCommand Sync(Action<object?> run, Func<object?, bool>? can = null) => new(p => { run(p); return Task.CompletedTask; }, can);
}
