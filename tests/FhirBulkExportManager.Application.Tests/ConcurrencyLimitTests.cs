using FhirBulkExportManager.Application.Configuration;
using FhirBulkExportManager.Domain;
using FhirBulkExportManager.Worker;

namespace FhirBulkExportManager.Application.Tests;

public sealed class ConcurrencyLimitTests
{
    [Fact]
    public void Configured_twenty_on_eight_cores_effectively_uses_eight()
    {
        Assert.Equal(8, ConcurrencyLimits.EffectiveExportLimit(configured: 20, processorCount: 8));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public void Invalid_per_site_limit_is_rejected(int configured)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ConcurrencyLimits.EffectiveSiteDownloadLimit(configured, pluginLimit: 4));
    }

    [Fact]
    public void Fair_selection_includes_both_sites_before_a_site_gets_second_slot()
    {
        var vendor = Guid.NewGuid();
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-03T00:00:00Z");
        var candidates = Enumerable.Range(0, 10).Select(index => CreateJob(vendor, siteA, index, now)).Concat(
            Enumerable.Range(10, 10).Select(index => CreateJob(vendor, siteB, index, now))).ToArray();

        var selected = FairSiteSelector.Select(candidates, 2);

        Assert.Equal(2, selected.Select(x => x.SiteId).Distinct().Count());
    }

    private static ExportJob CreateJob(Guid vendor, Guid site, int index, DateTimeOffset now) => new(
        Guid.NewGuid(), $"Job {index}", vendor, site, Guid.NewGuid(), "test", "1.0.0",
        ExportType.System, JobPriority.Normal, "C:\\Exports", now.AddSeconds(index));
}

