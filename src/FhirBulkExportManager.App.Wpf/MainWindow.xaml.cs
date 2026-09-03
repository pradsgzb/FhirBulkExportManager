using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace FhirBulkExportManager.App.Wpf;

public partial class MainWindow : Window
{
    private readonly Forms.NotifyIcon trayIcon;
    private bool exitConfirmed;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Enterprise FHIR Bulk Export Manager",
            Visible = false,
        };
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && ViewModel.MinimizeToTray)
            {
                Hide();
                trayIcon.Visible = true;
            }
        };
        Closing += OnClosing;
        Closed += (_, _) => trayIcon.Dispose();
    }

    public MainViewModel ViewModel { get; }

    private void ClientSecret_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            ViewModel.SetPassword(box.Password);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exitConfirmed)
        {
            return;
        }

        if (ViewModel.HasActiveJobs)
        {
            var result = System.Windows.MessageBox.Show(
                "Active export jobs exist.\n\nYes: exit and stop local monitoring/downloads. Remote exports are not cancelled.\nNo: minimize to the system tray and continue processing.\nCancel: return to the application.",
                "Active export jobs",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
                Hide();
                trayIcon.Visible = true;
                return;
            }

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
        }

        exitConfirmed = true;
        System.Windows.Application.Current.Shutdown();
    }

    private void RestoreFromTray()
    {
        trayIcon.Visible = false;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
