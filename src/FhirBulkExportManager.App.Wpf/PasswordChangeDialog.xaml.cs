using System.Windows;

namespace FhirBulkExportManager.App.Wpf;

public partial class PasswordChangeDialog : Window
{
    private readonly bool currentPasswordRequired;

    public PasswordChangeDialog(bool currentPasswordRequired)
    {
        InitializeComponent();
        this.currentPasswordRequired = currentPasswordRequired;
        CurrentPasswordPanel.Visibility = currentPasswordRequired ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => (currentPasswordRequired ? CurrentPasswordBox : NewPasswordBox).Focus();
    }

    public string CurrentPassword => CurrentPasswordBox.Password;
    public string NewPassword => NewPasswordBox.Password;

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (currentPasswordRequired && string.IsNullOrEmpty(CurrentPasswordBox.Password))
        {
            ValidationText.Text = "Enter the current password.";
            return;
        }

        if (NewPasswordBox.Password.Length is < 10 or > 256)
        {
            ValidationText.Text = "The new password must be between 10 and 256 characters.";
            return;
        }

        if (!string.Equals(NewPasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            ValidationText.Text = "The new password and confirmation do not match.";
            return;
        }

        DialogResult = true;
    }
}
