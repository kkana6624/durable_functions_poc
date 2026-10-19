using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Poc.Core;

public class BlobStorageRepository(IResultRepository results, BlobServiceClient? blobServiceClient = null, BlobContainerClient? containerClient = null) : IBlobStorageRepository
{
    private static readonly Regex ContainerNameRegex = new(@"\A[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]\z", RegexOptions.Compiled);

    public BlobStorageRepository(IResultRepository results) : this(results, null, null) { }
    public BlobStorageRepository(BlobContainerClient containerClient) : this(new BlobResultRepository(containerClient), null, containerClient) { }
    public BlobStorageRepository(BlobServiceClient blobServiceClient, BlobContainerClient containerClient) : this(new BlobResultRepository(containerClient), blobServiceClient, containerClient) { }

    public Func<ReadyManifest, CancellationToken, Task>? TargetBlobVerifier { get; set; }
    public Func<string, string, CancellationToken, Task<Stream>>? TaskOutputBlobStreamOpener { get; set; }
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Saves a validated ReadyManifest conditionally to inputs/{readyManifestId}/manifest.json
    /// after verifying that the referenced target blob exists and matches size and SHA-256.
    /// If an identical manifest already exists, succeeds idempotently.
    /// If a different manifest exists, throws WorkflowConflictException.
    /// </summary>
    public async Task<string> SaveReadyManifestAsync(ReadyManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        await VerifyTargetBlobAsync(manifest, cancellationToken);
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

        if (TargetBlobVerifier is not null)
        {
            await TargetBlobVerifier(manifest, cancellationToken);
            return;
        }

        if (results is ITargetBlobVerifier verifier)
        {
            await verifier.VerifyTargetBlobAsync(manifest, cancellationToken);
            return;
        }

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

    /// <summary>
    /// Reads and verifies a task output payload from the designated container and blob path using streaming download.
    /// Memory consumption is bounded by buffer size (80 KiB) and does not expand the entire blob into memory.
    /// </summary>
    public virtual async Task<BatchTaskOutputPayload> ReadTaskOutputAsync(string container, string blobPath, CancellationToken cancellationToken = default)
    {
        ValidateTargetBlobLocation(container, blobPath);

        Stream stream;
        if (TaskOutputBlobStreamOpener is not null)
        {
            stream = await TaskOutputBlobStreamOpener(container, blobPath, cancellationToken);
        }
        else if (results is ITaskOutputBlobReader reader)
        {
            stream = await reader.OpenTaskOutputBlobReadStreamAsync(container, blobPath, cancellationToken);
        }
        else if (blobServiceClient is not null)
        {
            var blobClient = blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            stream = response.Value.Content;
        }
        else if (containerClient is not null && string.Equals(containerClient.Name, container, StringComparison.OrdinalIgnoreCase))
        {
            var blobClient = containerClient.GetBlobClient(blobPath);
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
            stream = response.Value.Content;
        }
        else
        {
            throw new InvalidOperationException($"No suitable BlobServiceClient or BlobContainerClient configured to download task output blob from container '{container}'.");
        }

        await using (stream)
        {
            var (payload, _, _) = await ReadAndVerifyTaskOutputFromStreamAsync(stream, cancellationToken);
            return payload;
        }
    }

    /// <summary>
    /// Reads and verifies the output stream using an 80 KiB buffer for incremental SHA-256 calculation and size tracking,
    /// while deserializing BatchTaskOutputPayload without buffering the full content into memory.
    /// </summary>
    public static async Task<(BatchTaskOutputPayload Payload, string Sha256, long SizeBytes)> ReadAndVerifyTaskOutputFromStreamAsync(
        Stream contentStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentStream);

        await using var hashingStream = new HashingReadStream(contentStream);
        BatchTaskOutputPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<BatchTaskOutputPayload>(hashingStream, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Output blob could not be deserialized as BatchTaskOutputPayload: " + ex.Message, ex);
        }

        var drainBuffer = new byte[81920];
        while (await hashingStream.ReadAsync(drainBuffer.AsMemory(0, drainBuffer.Length), cancellationToken) > 0)
        {
        }

        if (payload is null)
            throw new InvalidDataException("Output blob was empty or could not be deserialized.");

        return (payload, hashingStream.HashHex, hashingStream.TotalBytesRead);
    }
}

public sealed class HashingReadStream(Stream inner) : Stream
{
    private readonly IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long totalBytesRead;

    public long TotalBytesRead => totalBytesRead;
    public string HashHex => Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.CanSeek ? inner.Length : totalBytesRead;
    public override long Position
    {
        get => totalBytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = inner.Read(buffer, offset, count);
        if (bytesRead > 0)
        {
            totalBytesRead += bytesRead;
            hasher.AppendData(buffer, offset, bytesRead);
        }
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int bytesRead = await inner.ReadAsync(buffer, cancellationToken);
        if (bytesRead > 0)
        {
            totalBytesRead += bytesRead;
            hasher.AppendData(buffer.Span.Slice(0, bytesRead));
        }
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int bytesRead = await inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (bytesRead > 0)
        {
            totalBytesRead += bytesRead;
            hasher.AppendData(buffer, offset, bytesRead);
        }
        return bytesRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hasher.Dispose();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        hasher.Dispose();
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
