using System.Windows;

namespace FhirBulkExportManager.App.Wpf.Presentation;

public interface IUserDialogService
{
    string? RequestTypedConfirmation(string title, string message, string requiredWord);
    bool Confirm(string title, string message);
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
}

public interface IWindowNavigationService
{
    Task ShowJobDetailAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class UserDialogService : IUserDialogService
{
    public string? RequestTypedConfirmation(string title, string message, string requiredWord)
    {
        var dialog = new TypedConfirmationDialog(title, message, requiredWord)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.ConfirmationText : null;
    }

    public bool Confirm(string title, string message) => System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message) => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
