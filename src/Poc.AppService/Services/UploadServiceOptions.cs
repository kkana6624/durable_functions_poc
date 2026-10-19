namespace Poc.AppService.Services;

public sealed class UploadServiceOptions
{
    public const string SectionName = "UploadService";

    /// <summary>
    /// Default blob container for workflow inputs (e.g. "workflow-input").
    /// </summary>
    public string DefaultContainer { get; set; } = "workflow-input";

    /// <summary>
    /// Maximum allowed upload size in bytes (default: 50 GiB).
    /// </summary>
    public long MaxUploadSizeBytes { get; set; } = 50L * 1024 * 1024 * 1024;
}
