using System.ComponentModel;
using System.Windows.Input;

namespace FhirBulkExportManager.Ui.Tests;

public sealed class UiCompositionTests
{
    [Fact]
    public void Main_view_model_exposes_mvvm_commands_and_notifications()
    {
        var type = typeof(FhirBulkExportManager.App.Wpf.MainViewModel);

        Assert.True(typeof(INotifyPropertyChanged).IsAssignableFrom(type));
        Assert.True(typeof(ICommand).IsAssignableFrom(type.GetProperty("StartCommand")!.PropertyType));
        Assert.True(typeof(ICommand).IsAssignableFrom(type.GetProperty("PauseCommand")!.PropertyType));
        Assert.True(typeof(ICommand).IsAssignableFrom(type.GetProperty("CancelRemoteCommand")!.PropertyType));
        Assert.True(typeof(IDisposable).IsAssignableFrom(type));
        Assert.True(typeof(ICommand).IsAssignableFrom(type.GetProperty("ChangePasswordCommand")!.PropertyType));
        Assert.True(typeof(ICommand).IsAssignableFrom(type.GetProperty("RestoreDefaultOptionsCommand")!.PropertyType));
        Assert.NotNull(type.GetProperty("AutoRefreshCountdownText"));
        Assert.NotNull(type.GetProperty("JobDashboard"));
    }
}
