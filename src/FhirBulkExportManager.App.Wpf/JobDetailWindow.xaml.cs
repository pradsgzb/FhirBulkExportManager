using System.Windows;

namespace FhirBulkExportManager.App.Wpf;

public partial class JobDetailWindow : Window
{
    public JobDetailWindow(JobDetailViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public JobDetailViewModel ViewModel { get; }
}

