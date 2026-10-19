using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Poc.AppService.Models;
using Poc.AppService.Services;
using Poc.Core;
using Xunit;

namespace Poc.AppService.Tests;

public class UploadServiceTests
{
    private static string ComputeSha256(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    [Fact]
    public void InitiateUpload_ValidRequest_ReturnsExpectedBlobKeyPattern()
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions
        {
            DefaultContainer = "workflow-input"
        });
        var service = new UploadService(repo, options);
        var owner = new WorkflowOwner("alice", "team-a");

        var request = new InitiateUploadRequest("dataset.csv", 1024, "text/csv");
        var response = service.InitiateUpload(request, owner);

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.InputId));
        Assert.Equal("workflow-input", response.Container);
        var expectedPrefix = $"owners/alice/inputs/{response.InputId}/dataset.csv";
        Assert.Equal(expectedPrefix, response.BlobKey);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/file.csv")]
    [InlineData("..\\windows.sys")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("file*name?.bin")]
    public void InitiateUpload_InvalidOrTraversingFilename_ThrowsArgumentException(string filename)
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions());
        var service = new UploadService(repo, options);
        var owner = new WorkflowOwner("alice");

        var request = new InitiateUploadRequest(filename, 100);
        Assert.Throws<ArgumentException>(() => service.InitiateUpload(request, owner));
    }

    [Fact]
    public void InitiateUpload_ExceedingMaxSize_ThrowsArgumentException()
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions
        {
            MaxUploadSizeBytes = 1000
        });
        var service = new UploadService(repo, options);
        var owner = new WorkflowOwner("alice");

        var request = new InitiateUploadRequest("big.dat", 1001);
        Assert.Throws<ArgumentException>(() => service.InitiateUpload(request, owner));
    }

    [Fact]
    public async Task FinalizeUpload_ValidBlob_CreatesAndSavesReadyManifest()
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions
        {
            DefaultContainer = "workflow-input"
        });
        var service = new UploadService(repo, options);
        var owner = new WorkflowOwner("alice", "org-x");

        var initResponse = service.InitiateUpload(new InitiateUploadRequest("input.dat", 5), owner);

        var blobBytes = Encoding.UTF8.GetBytes("hello");
        var sha256 = ComputeSha256(blobBytes);
        repo.Blobs[$"workflow-input/{initResponse.BlobKey}"] = blobBytes;

        var finalizeRequest = new FinalizeUploadRequest(
            InputId: initResponse.InputId,
            BlobKey: initResponse.BlobKey,
            ExpectedSizeBytes: blobBytes.Length,
            ExpectedSha256: sha256,
            VersionId: null,
            Etag: "\"test-etag\"");

        var finalizeResponse = await service.FinalizeUploadAsync(finalizeRequest, owner);

        Assert.NotNull(finalizeResponse);
        Assert.False(string.IsNullOrWhiteSpace(finalizeResponse.ReadyManifestId));
        Assert.Equal(initResponse.BlobKey, finalizeResponse.BlobKey);
        Assert.Equal(blobBytes.Length, finalizeResponse.SizeBytes);
        Assert.Equal(sha256, finalizeResponse.Sha256);

        // Verify ReadyManifest exists in storage
        var manifest = await repo.GetReadyManifestAsync(finalizeResponse.ReadyManifestId);
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("alice", manifest.OwnerId);
        Assert.Equal("org-x", manifest.OrgId);
        Assert.Equal("workflow-input", manifest.Blob.Container);
        Assert.Equal(initResponse.BlobKey, manifest.Blob.BlobKey);
        Assert.Equal(sha256, manifest.Blob.Sha256);
        Assert.Equal(blobBytes.Length, manifest.Blob.SizeBytes);
    }

    [Fact]
    public async Task FinalizeUpload_DifferentOwner_ThrowsWorkflowForbiddenException()
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions());
        var service = new UploadService(repo, options);

        var alice = new WorkflowOwner("alice");
        var bob = new WorkflowOwner("bob");

        var initResponse = service.InitiateUpload(new InitiateUploadRequest("input.dat", 5), alice);

        var blobBytes = Encoding.UTF8.GetBytes("hello");
        repo.Blobs[$"workflow-input/{initResponse.BlobKey}"] = blobBytes;

        // Bob attempts to finalize Alice's blob
        var finalizeRequest = new FinalizeUploadRequest(
            InputId: initResponse.InputId,
            BlobKey: initResponse.BlobKey,
            ExpectedSizeBytes: blobBytes.Length,
            ExpectedSha256: ComputeSha256(blobBytes));

        await Assert.ThrowsAsync<WorkflowForbiddenException>(() => service.FinalizeUploadAsync(finalizeRequest, bob));
    }

    [Fact]
    public async Task FinalizeUpload_BlobSizeMismatch_ThrowsInvalidDataException()
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions());
        var service = new UploadService(repo, options);
        var owner = new WorkflowOwner("alice");

        var initResponse = service.InitiateUpload(new InitiateUploadRequest("input.dat", 10), owner);

        var blobBytes = Encoding.UTF8.GetBytes("hello"); // 5 bytes instead of 10
        repo.Blobs[$"workflow-input/{initResponse.BlobKey}"] = blobBytes;

        var finalizeRequest = new FinalizeUploadRequest(
            InputId: initResponse.InputId,
            BlobKey: initResponse.BlobKey,
            ExpectedSizeBytes: 10,
            ExpectedSha256: ComputeSha256(blobBytes),
            Etag: "\"test\"");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.FinalizeUploadAsync(finalizeRequest, owner));
    }

    [Fact]
    public async Task FinalizeUpload_Sha256Mismatch_ThrowsInvalidDataException()
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions());
        var service = new UploadService(repo, options);
        var owner = new WorkflowOwner("alice");

        var initResponse = service.InitiateUpload(new InitiateUploadRequest("input.dat", 5), owner);

        var blobBytes = Encoding.UTF8.GetBytes("hello");
        repo.Blobs[$"workflow-input/{initResponse.BlobKey}"] = blobBytes;

        var finalizeRequest = new FinalizeUploadRequest(
            InputId: initResponse.InputId,
            BlobKey: initResponse.BlobKey,
            ExpectedSizeBytes: 5,
            ExpectedSha256: "0000000000000000000000000000000000000000000000000000000000000000",
            Etag: "\"test\"");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.FinalizeUploadAsync(finalizeRequest, owner));
    }

    [Fact]
    public async Task FinalizeUpload_MissingBlobInStorage_ThrowsFileNotFoundException()
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions());
        var service = new UploadService(repo, options);
        var owner = new WorkflowOwner("alice");

        var initResponse = service.InitiateUpload(new InitiateUploadRequest("input.dat", 5), owner);

        var finalizeRequest = new FinalizeUploadRequest(
            InputId: initResponse.InputId,
            BlobKey: initResponse.BlobKey,
            ExpectedSizeBytes: 5,
            ExpectedSha256: "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            Etag: "\"test\"");

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.FinalizeUploadAsync(finalizeRequest, owner));
    }

    [Fact]
    public async Task FinalizeUpload_ReplaySameInput_ReturnsIdenticalReadyManifestIdIdempotently()
    {
        var repo = new InMemoryBlobStorageRepository();
        var options = Options.Create(new UploadServiceOptions
        {
            DefaultContainer = "workflow-input"
        });
        var service = new UploadService(repo, options);
        var owner = new WorkflowOwner("alice", "org-x");

        var initResponse = service.InitiateUpload(new InitiateUploadRequest("input.dat", 5), owner);

        var blobBytes = Encoding.UTF8.GetBytes("hello");
        var sha256 = ComputeSha256(blobBytes);
        repo.Blobs[$"workflow-input/{initResponse.BlobKey}"] = blobBytes;

        var finalizeRequest = new FinalizeUploadRequest(
            InputId: initResponse.InputId,
            BlobKey: initResponse.BlobKey,
            ExpectedSizeBytes: blobBytes.Length,
            ExpectedSha256: sha256,
            VersionId: null,
            Etag: "\"test-etag\"");

        // First finalize call creates and persists manifest
        var response1 = await service.FinalizeUploadAsync(finalizeRequest, owner);
        Assert.NotNull(response1);
        Assert.False(string.IsNullOrWhiteSpace(response1.ReadyManifestId));
        Assert.StartsWith("rm-", response1.ReadyManifestId);

        // Delete blob from storage to verify the replayed call returns existing manifest without target blob re-fetch
        repo.Blobs.Clear();

        // Second finalize call (replay) must return the identical readyManifestId
        var response2 = await service.FinalizeUploadAsync(finalizeRequest, owner);
        Assert.NotNull(response2);
        Assert.Equal(response1.ReadyManifestId, response2.ReadyManifestId);
        Assert.Equal(response1.BlobKey, response2.BlobKey);
        Assert.Equal(response1.SizeBytes, response2.SizeBytes);
        Assert.Equal(response1.Sha256, response2.Sha256);
    }
}
