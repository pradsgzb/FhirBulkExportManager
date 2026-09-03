using System.Security.Principal;
using FhirBulkExportManager.Application.Abstractions;

namespace FhirBulkExportManager.Infrastructure.Security;

public sealed class WindowsIdentityProvider : IWindowsIdentityProvider
{
    public string GetCurrentIdentity()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsIdentity.GetCurrent().Name;
        }

        return Environment.UserName;
    }
}

