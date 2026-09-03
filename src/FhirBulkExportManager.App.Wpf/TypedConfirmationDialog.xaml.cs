using System.Windows;

namespace FhirBulkExportManager.App.Wpf;

public partial class TypedConfirmationDialog : Window
{
    private readonly string requiredWord;

    public TypedConfirmationDialog(string title, string message, string requiredWord)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        this.requiredWord = requiredWord;
        InstructionText.Text = $"Type {requiredWord} exactly to continue:";
        ConfirmationBox.Focus();
    }

    public string ConfirmationText => ConfirmationBox.Text;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(ConfirmationBox.Text.Trim(), requiredWord, StringComparison.Ordinal))
        {
            System.Windows.MessageBox.Show($"Type {requiredWord} exactly.", "Confirmation required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
