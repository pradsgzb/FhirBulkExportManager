using FhirBulkExportManager.Application.Services;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Plugin.EClinicalWorks;
using FhirBulkExportManager.Plugin.PublicR4;

namespace FhirBulkExportManager.ArchitectureTests;

public sealed class DependencyRuleTests
{
    [Fact]
    public void Domain_does_not_reference_infrastructure_ui_or_vendor_plugin()
    {
        var names = typeof(ExportJob).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();

        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", names);
        Assert.DoesNotContain("PresentationFramework", names);
        Assert.DoesNotContain("FhirBulkExportManager.Infrastructure", names);
        Assert.DoesNotContain("FhirBulkExportManager.Plugin.EClinicalWorks", names);
        Assert.DoesNotContain("FhirBulkExportManager.Plugin.PublicR4", names);
    }

    [Fact]
    public void Application_does_not_reference_wpf()
    {
        var names = typeof(ExportJobService).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();

        Assert.DoesNotContain("PresentationFramework", names);
        Assert.DoesNotContain("FhirBulkExportManager.App.Wpf", names);
    }

    [Fact]
    public void Vendor_plugin_does_not_reference_domain_or_persistence()
    {
        var pluginAssemblies = new[] { typeof(EClinicalWorksPlugin).Assembly, typeof(PublicFhirR4Plugin).Assembly };

        foreach (var assembly in pluginAssemblies)
        {
            var names = assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
            Assert.DoesNotContain("FhirBulkExportManager.Domain", names);
            Assert.DoesNotContain("FhirBulkExportManager.Persistence.Sqlite", names);
        }
    }
}
