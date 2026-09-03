using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace FhirBulkExportManager.Persistence.Sqlite.Migrations;

[DbContext(typeof(ExportDbContext))]
public sealed class ExportDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        InitialCreateModel.BuildModel(modelBuilder);
    }
}
