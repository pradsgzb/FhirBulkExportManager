using System.Windows.Input;

namespace FhirBulkExportManager.App.Wpf.Presentation;

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private int executing;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => Volatile.Read(ref executing) == 0 && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter) || Interlocked.Exchange(ref executing, 1) != 0)
        {
            return;
        }

        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref executing, 0);
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

