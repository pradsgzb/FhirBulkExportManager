using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace FhirBulkExportManager.App.Wpf;

public interface IApplicationRestartService
{
    void Restart();
}

public sealed class ApplicationRestartService : IApplicationRestartService
{
    public void Restart()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The application executable path could not be determined.");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
        };

        if (string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly))
            {
                throw new InvalidOperationException("The application entry assembly could not be determined.");
            }

            startInfo.ArgumentList.Add(entryAssembly);
        }

        startInfo.ArgumentList.Add($"--restart-after={Environment.ProcessId}");
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The replacement application process could not be started.");

        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
        {
            mainWindow.PrepareForRestart();
        }

        System.Windows.Application.Current.Shutdown();
    }
}
