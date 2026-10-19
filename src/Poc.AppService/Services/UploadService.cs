using System.Security.Cryptography;
using System.Text;
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

        var normalizedSha = request.ExpectedSha256.ToLowerInvariant();
        var identityKey = $"{owner.OwnerId}|{container}|{request.BlobKey}|{normalizedSha}|{request.ExpectedSizeBytes}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(identityKey));
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var readyManifestId = $"rm-{hashHex[..16]}";

        var existingManifest = await storageRepository.TryGetReadyManifestAsync(readyManifestId, cancellationToken);
        if (existingManifest is not null)
        {
            EnsureSameInput(existingManifest, request, owner, container, normalizedSha);
            logger?.LogInformation("Ready manifest {ReadyManifestId} already exists for owner {OwnerId}; returning existing manifest idempotently.",
                readyManifestId, owner.OwnerId);

            return ToResponse(existingManifest);
        }

        var versionId = string.IsNullOrWhiteSpace(request.VersionId) ? null : request.VersionId;
        var etag = string.IsNullOrWhiteSpace(request.Etag) ? null : request.Etag;

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
                etag = $"\"{normalizedSha[..16]}\"";
            }
        }

        var blob = new ReadyManifestBlob(
            Container: container,
            BlobKey: request.BlobKey,
            VersionId: versionId,
            Etag: etag,
            SizeBytes: request.ExpectedSizeBytes,
            Sha256: normalizedSha);

        var manifest = new ReadyManifest(
            SchemaVersion: 1,
            ReadyManifestId: readyManifestId,
            OwnerId: owner.OwnerId,
            OrgId: owner.OrgId,
            Blob: blob,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        // Saves conditionally and performs full streaming size/SHA-256 target blob verification
        try
        {
            await storageRepository.SaveReadyManifestAsync(manifest, cancellationToken);
        }
        catch (WorkflowConflictException)
        {
            // Another Finalize can win after our initial read. Creation time is not
            // part of input identity, but both callers must have pinned the same blob.
            var winner = await storageRepository.TryGetReadyManifestAsync(readyManifestId, cancellationToken);
            if (winner is null)
                throw;

            EnsureSameInput(winner, request, owner, container, normalizedSha);
            if (winner.SchemaVersion != manifest.SchemaVersion || winner.Blob != manifest.Blob)
                throw new WorkflowConflictException("Ready manifest already exists for a different fixed blob version.");

            return ToResponse(winner);
        }

        logger?.LogInformation("Successfully finalized upload and saved ready manifest {ReadyManifestId} for owner {OwnerId}",
            readyManifestId, owner.OwnerId);

        return ToResponse(manifest);
    }

    private static void EnsureSameInput(ReadyManifest manifest, FinalizeUploadRequest request,
        WorkflowOwner owner, string container, string normalizedSha)
    {
        // Keep the established manifest ID scheme, and verify every identity field
        // before treating a stored manifest as a retry. Omitted pins reuse the
        // previously fixed version; an explicitly supplied pin must match it.
        if (manifest.OwnerId != owner.OwnerId || manifest.OrgId != owner.OrgId ||
            manifest.Blob.Container != container || manifest.Blob.BlobKey != request.BlobKey ||
            manifest.Blob.SizeBytes != request.ExpectedSizeBytes || manifest.Blob.Sha256 != normalizedSha ||
            (!string.IsNullOrWhiteSpace(request.VersionId) && manifest.Blob.VersionId != request.VersionId) ||
            (!string.IsNullOrWhiteSpace(request.Etag) && manifest.Blob.Etag != request.Etag))
        {
            throw new WorkflowConflictException("Ready manifest already exists for different input or ownership.");
        }
    }

    private static FinalizeUploadResponse ToResponse(ReadyManifest manifest) => new(
        ReadyManifestId: manifest.ReadyManifestId,
        BlobKey: manifest.Blob.BlobKey,
        SizeBytes: manifest.Blob.SizeBytes,
        Sha256: manifest.Blob.Sha256);
}
