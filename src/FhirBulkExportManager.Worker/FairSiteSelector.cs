using FhirBulkExportManager.Domain;

namespace FhirBulkExportManager.Worker;

public static class FairSiteSelector
{
    public static IReadOnlyList<ExportJob> Select(IReadOnlyCollection<ExportJob> candidates, int maximum)
    {
        if (maximum <= 0 || candidates.Count == 0)
        {
            return [];
        }

        var queues = candidates
            .GroupBy(x => x.SiteId)
            .OrderBy(x => x.Key)
            .Select(group => new Queue<ExportJob>(group.OrderByDescending(x => x.Priority).ThenBy(x => x.NextPollAtUtc ?? x.CreatedAtUtc)))
            .ToList();
        var selected = new List<ExportJob>(Math.Min(maximum, candidates.Count));
        while (selected.Count < maximum && queues.Count > 0)
        {
            for (var index = queues.Count - 1; index >= 0 && selected.Count < maximum; index--)
            {
                var queue = queues[index];
                selected.Add(queue.Dequeue());
                if (queue.Count == 0)
                {
                    queues.RemoveAt(index);
                }
            }
        }

        return selected;
    }
}

