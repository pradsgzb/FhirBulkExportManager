using System.Windows;

namespace FhirBulkExportManager.App.Wpf;

public partial class PasswordPromptDialog : Window
{
    public PasswordPromptDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string Password => PasswordBox.Password;

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Password))
        {
            MessageText.Text = "Enter the application password to continue.";
            return;
        }

        DialogResult = true;
    }
}
