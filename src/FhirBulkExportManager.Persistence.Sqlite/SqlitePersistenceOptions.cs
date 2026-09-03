namespace FhirBulkExportManager.Persistence.Sqlite;

public sealed class SqlitePersistenceOptions
{
    public const string SectionName = "Persistence";

    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FhirBulkExportManager",
        "State",
        "exports.db");

    public int BusyTimeoutSeconds { get; set; } = 30;
}

