using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text.Json;

namespace Poc.Core;

public sealed class BlobResultRepository(BlobContainerClient container) : IResultRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string GetUri(string path) => GetBlob(path).Uri.AbsoluteUri;
    private BlobClient GetBlob(string path)
    {
        if ((!path.StartsWith("runs/poc-", StringComparison.Ordinal) && !path.StartsWith("inputs/", StringComparison.Ordinal)) || path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Blob path must be scoped to a PoC run or inputs.", nameof(path));
        return container.GetBlobClient(path);
    }
    public async Task<string> PutIfAbsentAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var blob = GetBlob(path);
        var content = BinaryData.FromObjectAsJson(value, JsonOptions);
        try
        {
            await blob.UploadAsync(content, new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } }, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            var existing = await blob.DownloadContentAsync(cancellationToken);
            // Documents are deterministic; byte equality also catches unexpected fields/tampering.
            if (!existing.Value.Content.ToMemory().Span.SequenceEqual(content.ToMemory().Span))
            {
                // Fallback: Check semantic equality when byte equality differs (e.g. legacy schema evolution
                // where optional/default fields were omitted in stored JSON).
                bool semanticallyEqual = false;
                try
                {
                    var existingRecord = existing.Value.Content.ToObjectFromJson<T>(JsonOptions);
                    semanticallyEqual = EqualityComparer<T>.Default.Equals(existingRecord, value);
                }
                catch
                {
                    semanticallyEqual = false;
                }

                if (!semanticallyEqual)
                    throw new WorkflowConflictException($"Existing blob has different content: {path}");
            }
        }
        return blob.Uri.AbsoluteUri;
    }
    public async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        var content = await GetBlob(path).DownloadContentAsync(cancellationToken);
        return content.Value.Content.ToObjectFromJson<T>(JsonOptions) ?? throw new InvalidDataException($"Empty JSON at {path}");
    }
}
