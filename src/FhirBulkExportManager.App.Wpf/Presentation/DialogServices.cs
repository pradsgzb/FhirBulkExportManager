using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace FhirBulkExportManager.App.Wpf.Presentation;

public interface IUserDialogService
{
    string? RequestTypedConfirmation(string title, string message, string requiredWord);
    string? RequestPassword(string title, string message);
    PasswordChangeRequest? RequestPasswordChange(bool currentPasswordRequired);
    string? SelectFolder(string title, string currentPath);
    DataRootMigrationChoice RequestDataRootMigration(string sourcePath, string targetPath);
    bool Confirm(string title, string message);
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
}

public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);

public enum DataRootMigrationChoice
{
    Cancel,
    MoveExistingData,
    LeaveExistingData,
}

public interface IWindowNavigationService
{
    Task ShowJobDetailAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class UserDialogService : IUserDialogService
{
    public string? RequestTypedConfirmation(string title, string message, string requiredWord)
    {
        var dialog = new TypedConfirmationDialog(title, message, requiredWord);
        AssignOwnerWhenAvailable(dialog);
        return dialog.ShowDialog() == true ? dialog.ConfirmationText : null;
    }

    public string? RequestPassword(string title, string message)
    {
        var dialog = new PasswordPromptDialog(title, message);
        AssignOwnerWhenAvailable(dialog);
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    public PasswordChangeRequest? RequestPasswordChange(bool currentPasswordRequired)
    {
        var dialog = new PasswordChangeDialog(currentPasswordRequired);
        AssignOwnerWhenAvailable(dialog);
        return dialog.ShowDialog() == true
            ? new PasswordChangeRequest(dialog.CurrentPassword, dialog.NewPassword)
            : null;
    }

    public string? SelectFolder(string title, string currentPath)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = title,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(currentPath) ? currentPath : string.Empty,
        };
        return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public DataRootMigrationChoice RequestDataRootMigration(string sourcePath, string targetPath)
    {
        var result = System.Windows.MessageBox.Show(
            "The current managed data root contains files or folders.\n\n" +
            $"Current: {sourcePath}\n" +
            $"New: {targetPath}\n\n" +
            "Yes: move all existing managed data during the next application restart. The destination must be empty.\n" +
            "No: leave the existing folder unchanged and begin using the new location after restart. Existing downloaded data will not be available from the new root.\n" +
            "Cancel: do not save this change.",
            "Move existing managed data?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => DataRootMigrationChoice.MoveExistingData,
            MessageBoxResult.No => DataRootMigrationChoice.LeaveExistingData,
            _ => DataRootMigrationChoice.Cancel,
        };
    }

    public bool Confirm(string title, string message) => System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message) => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private static void AssignOwnerWhenAvailable(Window dialog)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is FhirBulkExportManager.App.Wpf.MainWindow { IsLoaded: true } &&
            !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
    }
}
