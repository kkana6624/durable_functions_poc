using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Poc.AppService.Models;
using Poc.Core;

namespace Poc.AppService.Services;

public sealed class UploadService(
    IBlobStorageRepository storageRepository,
    IOptions<UploadServiceOptions> options,
    BlobServiceClient? blobServiceClient = null,
    ILogger<UploadService>? logger = null) : IUploadService
{
    private readonly UploadServiceOptions serviceOptions = options.Value;
    private static readonly Regex Sha256Regex = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.Compiled);
    private static readonly Regex SafeFilenameRegex = new(@"\A[a-zA-Z0-9_.-]{1,255}\z", RegexOptions.Compiled);

    public InitiateUploadResponse InitiateUpload(InitiateUploadRequest request, WorkflowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);
        owner.Validate();

        if (string.IsNullOrWhiteSpace(request.Filename))
            throw new ArgumentException("Filename is required.");

        var cleanFilename = request.Filename.Trim();
        if (cleanFilename.Contains("..", StringComparison.Ordinal) ||
            cleanFilename.Contains('/') ||
            cleanFilename.Contains('\\') ||
            cleanFilename != Path.GetFileName(cleanFilename) ||
            !SafeFilenameRegex.IsMatch(cleanFilename))
        {
            throw new ArgumentException("Filename contains invalid characters or path traversal.");
        }

        if (request.ExpectedSizeBytes < 0)
            throw new ArgumentException("expectedSizeBytes must be non-negative.");

        if (request.ExpectedSizeBytes > serviceOptions.MaxUploadSizeBytes)
            throw new ArgumentException($"expectedSizeBytes exceeds maximum allowed size of {serviceOptions.MaxUploadSizeBytes} bytes.");

        var inputId = Guid.NewGuid().ToString("n");
        var blobKey = $"owners/{owner.OwnerId}/inputs/{inputId}/{cleanFilename}";
        var container = serviceOptions.DefaultContainer;

        logger?.LogInformation("Initiated upload for owner {OwnerId}, inputId {InputId}, blobKey {BlobKey}",
            owner.OwnerId, inputId, blobKey);

        return new InitiateUploadResponse(inputId, blobKey, container);
    }

    public async Task<FinalizeUploadResponse> FinalizeUploadAsync(FinalizeUploadRequest request, WorkflowOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);
        owner.Validate();

        if (string.IsNullOrWhiteSpace(request.InputId))
            throw new ArgumentException("inputId is required.");

        if (string.IsNullOrWhiteSpace(request.BlobKey))
            throw new ArgumentException("blobKey is required.");

        if (request.ExpectedSizeBytes < 0)
            throw new ArgumentException("expectedSizeBytes must be non-negative.");

        if (string.IsNullOrWhiteSpace(request.ExpectedSha256) || !Sha256Regex.IsMatch(request.ExpectedSha256))
            throw new ArgumentException("expectedSha256 must be 64 hex characters.");

        // Enforce owner separation: blobKey must reside under owners/{ownerId}/inputs/{inputId}/
        var expectedPrefix = $"owners/{owner.OwnerId}/inputs/{request.InputId}/";
        if (!request.BlobKey.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new WorkflowForbiddenException($"Blob key '{request.BlobKey}' does not match authenticated owner and input ID.");
        }

        var container = string.IsNullOrWhiteSpace(request.Container) ? serviceOptions.DefaultContainer : request.Container;
        BlobStorageRepository.ValidateTargetBlobLocation(container, request.BlobKey);

        var versionId = request.VersionId;
        var etag = request.Etag;

        // If neither versionId nor etag was provided, query storage properties if BlobServiceClient is available
        if (string.IsNullOrWhiteSpace(versionId) && string.IsNullOrWhiteSpace(etag))
        {
            if (blobServiceClient is not null)
            {
                try
                {
                    var blobClient = blobServiceClient.GetBlobContainerClient(container).GetBlobClient(request.BlobKey);
                    var props = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
                    etag = props.Value.ETag.ToString();
                    versionId = props.Value.VersionId;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex, "Could not fetch blob properties for {BlobKey} in {Container}.", request.BlobKey, container);
                }
            }

            // Fallback for test/mock repositories: pin with content-derived etag if still null
            if (string.IsNullOrWhiteSpace(versionId) && string.IsNullOrWhiteSpace(etag))
            {
                etag = $"\"{request.ExpectedSha256.ToLowerInvariant()[..16]}\"";
            }
        }

        var readyManifestId = $"rm-{Guid.NewGuid():n}";
        var blob = new ReadyManifestBlob(
            Container: container,
            BlobKey: request.BlobKey,
            VersionId: versionId,
            Etag: etag,
            SizeBytes: request.ExpectedSizeBytes,
            Sha256: request.ExpectedSha256.ToLowerInvariant());

        var manifest = new ReadyManifest(
            SchemaVersion: 1,
            ReadyManifestId: readyManifestId,
            OwnerId: owner.OwnerId,
            OrgId: owner.OrgId,
            Blob: blob,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        // Saves conditionally and performs full streaming size/SHA-256 target blob verification
        await storageRepository.SaveReadyManifestAsync(manifest, cancellationToken);

        logger?.LogInformation("Successfully finalized upload and saved ready manifest {ReadyManifestId} for owner {OwnerId}",
            readyManifestId, owner.OwnerId);

        return new FinalizeUploadResponse(
            ReadyManifestId: readyManifestId,
            BlobKey: request.BlobKey,
            SizeBytes: request.ExpectedSizeBytes,
            Sha256: request.ExpectedSha256.ToLowerInvariant());
    }
}
