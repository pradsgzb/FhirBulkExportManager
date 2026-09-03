using System.Windows;
using System.Windows.Threading;

namespace FhirBulkExportManager.App.Wpf;

public partial class TransientNotificationWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };

    public TransientNotificationWindow(string operation)
    {
        InitializeComponent();
        MessageText.Text = $"The server returned HTTP 400 Bad Request while {operation}. Check whether the server requires an approved VPN, network location, or IP whitelist. Also verify the configured endpoint, scope, and audience.";
        Loaded += OnLoaded;
        Closed += (_, _) => timer.Stop();
        timer.Tick += (_, _) => Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - ActualWidth - 18);
        Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight - 18);
        timer.Start();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
