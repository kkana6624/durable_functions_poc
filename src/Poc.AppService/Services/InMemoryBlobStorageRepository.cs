using System.Collections.Concurrent;
using System.Text.Json;
using Poc.Core;

namespace Poc.AppService.Services;

/// <summary>
/// In-memory implementation of IBlobStorageRepository and IResultRepository for local development and testing.
/// </summary>
public sealed class InMemoryBlobStorageRepository : IBlobStorageRepository, IResultRepository, ITargetBlobVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public ConcurrentDictionary<string, string> Documents { get; } = new();
    public ConcurrentDictionary<string, byte[]> Blobs { get; } = new();

    public string GetUri(string path) => $"https://127.0.0.1/local-blobs/{path}";

    public Task<string> PutIfAbsentAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (Documents.TryGetValue(path, out var existing))
        {
            if (existing != json)
            {
                bool semanticallyEqual = false;
                try
                {
                    var existingObj = JsonSerializer.Deserialize<T>(existing, JsonOptions);
                    semanticallyEqual = EqualityComparer<T>.Default.Equals(existingObj, value);
                }
                catch { }

                if (!semanticallyEqual)
                    throw new WorkflowConflictException("Existing document differs.");
            }
        }
        else
        {
            if (!Documents.TryAdd(path, json))
            {
                if (Documents.TryGetValue(path, out existing) && existing != json)
                    throw new WorkflowConflictException("Existing document differs.");
            }
        }
        return Task.FromResult(GetUri(path));
    }

    public Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (Documents.TryGetValue(path, out var json))
        {
            var obj = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return Task.FromResult(obj!);
        }
        return Task.FromException<T>(new FileNotFoundException($"Document '{path}' not found."));
    }

    public async Task VerifyTargetBlobAsync(ReadyManifest manifest, CancellationToken cancellationToken = default)
    {
        var key = $"{manifest.Blob.Container}/{manifest.Blob.BlobKey}";
        if (!Blobs.TryGetValue(key, out var bytes))
        {
            throw new FileNotFoundException($"Target blob '{manifest.Blob.BlobKey}' not found in container '{manifest.Blob.Container}'.");
        }
        using var stream = new MemoryStream(bytes);
        await BlobStorageRepository.VerifyBlobContentAsync(stream, manifest.Blob.SizeBytes, manifest.Blob.Sha256, cancellationToken);
    }

    public Task<string> SaveReadyManifestAsync(ReadyManifest manifest, CancellationToken cancellationToken = default) =>
        new BlobStorageRepository(this).SaveReadyManifestAsync(manifest, cancellationToken);

    public Task<ReadyManifest?> TryGetReadyManifestAsync(string readyManifestId, CancellationToken cancellationToken = default) =>
        new BlobStorageRepository(this).TryGetReadyManifestAsync(readyManifestId, cancellationToken);

    public Task<ReadyManifest> GetReadyManifestAsync(string readyManifestId, CancellationToken cancellationToken = default) =>
        new BlobStorageRepository(this).GetReadyManifestAsync(readyManifestId, cancellationToken);

    public Task<BatchTaskOutputPayload> ReadTaskOutputAsync(string container, string blobPath, CancellationToken cancellationToken = default)
    {
        var key = $"{container}/{blobPath}";
        if (Documents.TryGetValue(key, out var json) || Documents.TryGetValue(blobPath, out json))
        {
            var payload = JsonSerializer.Deserialize<BatchTaskOutputPayload>(json, JsonOptions);
            return Task.FromResult(payload!);
        }
        return Task.FromException<BatchTaskOutputPayload>(new FileNotFoundException($"Task output '{blobPath}' not found."));
    }
}
