namespace FhirBulkExportManager.Domain;

public sealed class ExportJobEvent
{
    private ExportJobEvent()
    {
    }

    public ExportJobEvent(
        Guid exportJobId,
        ExportJobState previousState,
        ExportJobState newState,
        string eventType,
        DateTimeOffset timestampUtc,
        string message,
        Guid operationId,
        string correlationId,
        string metadataJson = "{}")
    {
        Id = Guid.NewGuid();
        ExportJobId = exportJobId;
        PreviousState = previousState;
        NewState = newState;
        EventType = eventType;
        TimestampUtc = timestampUtc;
        Message = message;
        OperationId = operationId;
        CorrelationId = correlationId;
        MetadataJson = metadataJson;
    }

    public Guid Id { get; private set; }
    public Guid ExportJobId { get; private set; }
    public ExportJobState PreviousState { get; private set; }
    public ExportJobState NewState { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public Guid OperationId { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string MetadataJson { get; private set; } = "{}";
}

