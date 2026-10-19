using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Moq;
using Poc.AppService.Models;
using Poc.AppService.Services;
using Poc.Core;
using Xunit;

namespace Poc.AppService.Tests;

public class UploadFinalizeIdentityTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("hello");
    private static readonly WorkflowOwner Owner = new("alice", "org-x");
    private static readonly FinalizeUploadRequest Request = new(
        InputId: "input-1", BlobKey: "owners/alice/inputs/input-1/input.dat",
        ExpectedSizeBytes: Content.Length,
        ExpectedSha256: Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant(),
        VersionId: "version-1", Etag: "\"etag-1\"");

    private static UploadService CreateService(IBlobStorageRepository repository) =>
        new(repository, Options.Create(new UploadServiceOptions()));

    private static InMemoryBlobStorageRepository CreateRepository()
    {
        var repository = new InMemoryBlobStorageRepository();
        repository.Blobs[$"workflow-input/{Request.BlobKey}"] = Content;
        return repository;
    }

    [Theory]
    [InlineData("version-2", "\"etag-1\"")]
    [InlineData("version-1", "\"etag-2\"")]
    [InlineData("version-2", null)]
    [InlineData(null, "\"etag-2\"")]
    public async Task Replay_ChangedExplicitPin_Conflicts(string? versionId, string? etag)
    {
        var repository = CreateRepository();
        var service = CreateService(repository);
        var original = await service.FinalizeUploadAsync(Request, Owner);
        repository.Blobs.Clear();

        await Assert.ThrowsAsync<WorkflowConflictException>(() => service.FinalizeUploadAsync(
            Request with { VersionId = versionId, Etag = etag }, Owner));

        var manifest = await repository.GetReadyManifestAsync(original.ReadyManifestId);
        Assert.Equal(Request.VersionId, manifest.Blob.VersionId);
        Assert.Equal(Request.Etag, manifest.Blob.Etag);
        Assert.Single(repository.Documents);
    }

    [Fact]
    public async Task Replay_ChangedOrganization_Conflicts()
    {
        var repository = CreateRepository();
        var service = CreateService(repository);
        await service.FinalizeUploadAsync(Request, Owner);

        await Assert.ThrowsAsync<WorkflowConflictException>(() => service.FinalizeUploadAsync(
            Request, Owner with { OrgId = "another-org" }));
        Assert.Single(repository.Documents);
    }

    [Fact]
    public async Task Replay_OmittedPins_ReusesPreviouslyFixedVersion()
    {
        var repository = CreateRepository();
        var service = CreateService(repository);
        var original = await service.FinalizeUploadAsync(Request, Owner);
        repository.Blobs.Clear();

        var replay = await service.FinalizeUploadAsync(Request with
        {
            VersionId = null, Etag = null, ExpectedSha256 = Request.ExpectedSha256.ToUpperInvariant()
        }, Owner);

        Assert.Equal(original, replay);
        var manifest = await repository.GetReadyManifestAsync(replay.ReadyManifestId);
        Assert.Equal(Request.VersionId, manifest.Blob.VersionId);
        Assert.Equal(Request.Etag, manifest.Blob.Etag);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("container")]
    [InlineData("key")]
    [InlineData("size")]
    [InlineData("sha256")]
    public async Task Replay_StoredManifestIdentityMismatch_Conflicts(string field)
    {
        var repository = CreateRepository();
        var original = await CreateService(repository).FinalizeUploadAsync(Request, Owner);
        var manifest = await repository.GetReadyManifestAsync(original.ReadyManifestId);
        var conflicting = field switch
        {
            "owner" => manifest with { OwnerId = "bob" },
            "container" => manifest with { Blob = manifest.Blob with { Container = "other-input" } },
            "key" => manifest with { Blob = manifest.Blob with { BlobKey = "another.dat" } },
            "size" => manifest with { Blob = manifest.Blob with { SizeBytes = 6 } },
            _ => manifest with { Blob = manifest.Blob with { Sha256 = new string('0', 64) } }
        };
        var mock = new Mock<IBlobStorageRepository>(MockBehavior.Strict);
        mock.Setup(x => x.TryGetReadyManifestAsync(original.ReadyManifestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(conflicting);

        await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            CreateService(mock.Object).FinalizeUploadAsync(Request, Owner));
    }

    [Fact]
    public async Task ConcurrentFinalize_SameInputWithDifferentCreationTimes_ReturnsWinningManifest()
    {
        var repository = CreateRepository();
        var mock = new Mock<IBlobStorageRepository>(MockBehavior.Strict);
        var bothInitialReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new ConcurrentBag<ReadyManifest>();
        var readCount = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;

        mock.Setup(x => x.TryGetReadyManifestAsync(It.IsAny<string>(), token))
            .Returns(async (string id, CancellationToken ct) =>
            {
                // Snapshot both absent reads before permitting either caller to save.
                var existing = await repository.TryGetReadyManifestAsync(id, ct);
                var read = Interlocked.Increment(ref readCount);
                if (read == 2) bothInitialReads.TrySetResult();
                if (read <= 2) await bothInitialReads.Task.WaitAsync(ct);
                if (read == 2)
                {
                    await firstSave.Task.WaitAsync(ct);
                    await Task.Delay(20, ct); // Ensure distinct persisted creation times.
                }
                return existing;
            });
        mock.Setup(x => x.SaveReadyManifestAsync(It.IsAny<ReadyManifest>(), token))
            .Returns(async (ReadyManifest manifest, CancellationToken ct) =>
            {
                candidates.Add(manifest);
                var uri = await repository.SaveReadyManifestAsync(manifest, ct);
                firstSave.TrySetResult();
                return uri;
            });
        var service = CreateService(mock.Object);

        var responses = await Task.WhenAll(
            service.FinalizeUploadAsync(Request, Owner, token),
            service.FinalizeUploadAsync(Request, Owner, token));

        Assert.Equal(responses[0], responses[1]);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(2, candidates.Select(x => x.CreatedAtUtc).Distinct().Count());
        Assert.Single(repository.Documents);
        Assert.Equal(3, readCount); // Two initial reads plus the losing caller's winner read.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalSaveConflict_DifferentWinnerPin_StillConflicts(bool omitRequestPins)
    {
        var mock = new Mock<IBlobStorageRepository>(MockBehavior.Strict);
        ReadyManifest? winner = null;
        mock.Setup(x => x.TryGetReadyManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) => Task.FromResult(winner));
        mock.Setup(x => x.SaveReadyManifestAsync(It.IsAny<ReadyManifest>(), It.IsAny<CancellationToken>()))
            .Returns((ReadyManifest candidate, CancellationToken _) =>
            {
                winner = candidate with
                {
                    Blob = candidate.Blob with { Etag = "\"different-fixed-version\"" },
                    CreatedAtUtc = candidate.CreatedAtUtc.AddMinutes(-1)
                };
                throw new WorkflowConflictException("Another caller saved first.");
            });
        var request = omitRequestPins ? Request with { VersionId = null, Etag = null } : Request;

        await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            CreateService(mock.Object).FinalizeUploadAsync(request, Owner));
    }

    [Fact]
    public async Task ConditionalSaveConflict_NoWinner_PropagatesConflict()
    {
        var mock = new Mock<IBlobStorageRepository>(MockBehavior.Strict);
        mock.Setup(x => x.TryGetReadyManifestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReadyManifest?)null);
        var original = new WorkflowConflictException("Unrelated conflict.");
        mock.Setup(x => x.SaveReadyManifestAsync(It.IsAny<ReadyManifest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(original);

        var actual = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            CreateService(mock.Object).FinalizeUploadAsync(Request, Owner));
        Assert.Same(original, actual);
    }
}
