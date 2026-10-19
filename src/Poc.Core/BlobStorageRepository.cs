using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Poc.Core;

public sealed class BlobStorageRepository(IResultRepository results, BlobServiceClient? blobServiceClient = null, BlobContainerClient? containerClient = null)
{
    private static readonly Regex ContainerNameRegex = new(@"\A[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]\z", RegexOptions.Compiled);

    public BlobStorageRepository(IResultRepository results) : this(results, null, null) { }
    public BlobStorageRepository(BlobContainerClient containerClient) : this(new BlobResultRepository(containerClient), null, containerClient) { }
    public BlobStorageRepository(BlobServiceClient blobServiceClient, BlobContainerClient containerClient) : this(new BlobResultRepository(containerClient), blobServiceClient, containerClient) { }

    /// <summary>
    /// Saves a validated ReadyManifest conditionally to inputs/{readyManifestId}/manifest.json.
    /// If an identical manifest already exists, succeeds idempotently.
    /// If a different manifest exists, throws WorkflowConflictException.
    /// </summary>
    public async Task<string> SaveReadyManifestAsync(ReadyManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        var path = WorkflowIdentity.ReadyManifestPath(manifest.ReadyManifestId);
        return await results.PutIfAbsentAsync(path, manifest, cancellationToken);
    }

    /// <summary>
    /// Retrieves a ReadyManifest from inputs/{readyManifestId}/manifest.json.
    /// Returns null if not found.
    /// Throws InvalidDataException if the manifest is invalid or its readyManifestId does not match.
    /// </summary>
    public async Task<ReadyManifest?> TryGetReadyManifestAsync(string readyManifestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(readyManifestId)) return null;
        var path = WorkflowIdentity.ReadyManifestPath(readyManifestId);
        try
        {
            var manifest = await results.ReadAsync<ReadyManifest>(path, cancellationToken);
            if (manifest is not null)
            {
                manifest.Validate();
                if (manifest.ReadyManifestId != readyManifestId)
                    throw new InvalidDataException("Ready manifest ID mismatch.");
            }
            return manifest;
        }
        catch (FileNotFoundException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>
    /// Retrieves a ReadyManifest from inputs/{readyManifestId}/manifest.json.
    /// Throws FileNotFoundException if not found.
    /// </summary>
    public async Task<ReadyManifest> GetReadyManifestAsync(string readyManifestId, CancellationToken cancellationToken = default)
    {
        return await TryGetReadyManifestAsync(readyManifestId, cancellationToken)
            ?? throw new FileNotFoundException($"Ready manifest '{readyManifestId}' not found.");
    }

    /// <summary>
    /// Validates the container name and blobKey to ensure they adhere to Azure naming rules
    /// and prevent directory traversal or absolute path injection.
    /// </summary>
    public static void ValidateTargetBlobLocation(string container, string blobKey)
    {
        if (string.IsNullOrWhiteSpace(container) || !ContainerNameRegex.IsMatch(container))
            throw new ArgumentException("Container name must be a valid Azure Storage container name (3–63 lowercase letters, digits, and hyphens).", nameof(container));
        if (string.IsNullOrWhiteSpace(blobKey) || blobKey.StartsWith('/') || blobKey.StartsWith('\\') || blobKey.Contains("..", StringComparison.Ordinal) || blobKey.Contains('\\'))
            throw new ArgumentException("Blob key must be a relative path and cannot contain directory traversal or backslashes.", nameof(blobKey));
    }

    /// <summary>
    /// Verifies the content of a blob stream against expected sizeBytes and SHA-256 hash using streaming read
    /// without loading the entire payload into memory.
    /// </summary>
    public static async Task VerifyBlobContentAsync(Stream contentStream, long expectedSizeBytes, string expectedSha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentStream);
        if (expectedSizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(expectedSizeBytes), "expectedSizeBytes must be non-negative.");
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64)
            throw new ArgumentException("expectedSha256 must be 64 hex characters.", nameof(expectedSha256));

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            totalBytesRead += bytesRead;
            if (totalBytesRead > expectedSizeBytes)
            {
                throw new InvalidDataException($"Blob size exceeded expected size of {expectedSizeBytes} bytes.");
            }
            hasher.AppendData(buffer, 0, bytesRead);
        }

        if (totalBytesRead != expectedSizeBytes)
            throw new InvalidDataException($"Blob size mismatch. Expected {expectedSizeBytes} bytes, but found {totalBytesRead} bytes.");

        var computedSha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(computedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Blob SHA-256 mismatch. Expected {expectedSha256}, but computed {computedSha256}.");
    }

    /// <summary>
    /// Verifies the target blob designated by ReadyManifest in Azure Storage using streaming content verification.
    /// </summary>
    public async Task VerifyTargetBlobAsync(ReadyManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        ValidateTargetBlobLocation(manifest.Blob.Container, manifest.Blob.BlobKey);

        BlobClient blobClient;
        if (blobServiceClient is not null)
        {
            blobClient = blobServiceClient.GetBlobContainerClient(manifest.Blob.Container).GetBlobClient(manifest.Blob.BlobKey);
        }
        else if (containerClient is not null && string.Equals(containerClient.Name, manifest.Blob.Container, StringComparison.OrdinalIgnoreCase))
        {
            blobClient = containerClient.GetBlobClient(manifest.Blob.BlobKey);
        }
        else
        {
            throw new InvalidOperationException("No suitable BlobServiceClient or BlobContainerClient configured to download target blob.");
        }

        if (!string.IsNullOrEmpty(manifest.Blob.VersionId))
        {
            blobClient = blobClient.WithVersion(manifest.Blob.VersionId);
        }

        BlobDownloadOptions downloadOptions = new();
        if (!string.IsNullOrEmpty(manifest.Blob.Etag))
        {
            downloadOptions.Conditions = new BlobRequestConditions { IfMatch = new ETag(manifest.Blob.Etag) };
        }

        var response = await blobClient.DownloadStreamingAsync(downloadOptions, cancellationToken);
        await using var stream = response.Value.Content;
        await VerifyBlobContentAsync(stream, manifest.Blob.SizeBytes, manifest.Blob.Sha256, cancellationToken);
    }
}
