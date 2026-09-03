using System.Reflection;
using System.Runtime.Loader;
using FhirBulkExportManager.PluginContracts;

namespace FhirBulkExportManager.PluginHost;

internal sealed class PluginLoadContext(string pluginAssemblyPath) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver resolver = new(pluginAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, typeof(IVendorPlugin).Assembly.GetName().Name, StringComparison.Ordinal))
        {
            return null;
        }

        var path = resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

