namespace FhirBulkExportManager.Shared;

public enum ErrorKind
{
    Authentication,
    Authorization,
    Configuration,
    NetworkTransient,
    RemoteRateLimit,
    RemoteServer,
    RemotePermanent,
    ManifestValidation,
    Download,
    FileValidation,
    Storage,
    Database,
    Plugin,
    Cancellation,
    UnknownRemoteState,
}

public sealed record AppError(ErrorKind Kind, string Code, string Message, bool IsTransient, string? TechnicalDetail = null)
{
    public AppError Sanitize() => this with
    {
        Message = SecurityRedactor.Redact(Message),
        TechnicalDetail = SecurityRedactor.Redact(TechnicalDetail),
    };
}

public readonly record struct OperationResult(bool Succeeded, AppError? Error)
{
    public static OperationResult Success() => new(true, null);

    public static OperationResult Failure(AppError error) => new(false, error.Sanitize());
}

