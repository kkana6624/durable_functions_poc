using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Poc.Core;
using Poc.Functions;
using Xunit;

namespace Poc.Tests;

public class ReadyManifestTests
{
    // SHA-256 of 1024 ASCII 'a' bytes
    private const string SampleSha256 = "2edc986847e209b4016e141a6dc8716d3207350f416969382d431539bf292e4a";

    private static ReadyManifest CreateValidManifest(string id = "input-manifest-001", string? ownerId = "alice", string? orgId = "finance")
    {
        return new ReadyManifest(
            SchemaVersion: 1,
            ReadyManifestId: id,
            OwnerId: ownerId,
            OrgId: orgId,
            Blob: new ReadyManifestBlob(
                Container: "workflow-input",
                BlobKey: "owners/alice/inputs/batch-01/input.dat",
                VersionId: "2026-10-19T00:00:00.0000000Z",
                Etag: "\"0x8D9...\"",
                SizeBytes: 1024,
                Sha256: SampleSha256),
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static byte[] AddValidTestBlob(InMemoryResultRepository repo, ReadyManifest manifest, byte[]? customData = null)
    {
        var data = customData ?? Encoding.UTF8.GetBytes(new string('a', (int)manifest.Blob.SizeBytes));
        repo.Blobs[$"{manifest.Blob.Container}/{manifest.Blob.BlobKey}"] = data;
        return data;
    }

    [Fact]
    public void ReadyManifest_Validation_SucceedsForValidManifest()
    {
        var manifest = CreateValidManifest();
        manifest.Validate();
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("input-manifest-001", manifest.ReadyManifestId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void ReadyManifest_Validation_FailsOnInvalidSchemaVersion(int version)
    {
        var manifest = CreateValidManifest() with { SchemaVersion = version };
        var ex = Assert.Throws<ArgumentException>(() => manifest.Validate());
        Assert.Contains("schemaVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-invalid-prefix")]
    [InlineData("manifest with spaces")]
    [InlineData("manifest@symbol")]
    public void ReadyManifest_Validation_FailsOnInvalidReadyManifestId(string invalidId)
    {
        var manifest = CreateValidManifest() with { ReadyManifestId = invalidId };
        var ex = Assert.Throws<ArgumentException>(() => manifest.Validate());
        Assert.Contains("readyManifestId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadyManifest_Validation_FailsOnInvalidOwnerOrOrg()
    {
        // Org without Owner
        var orgWithoutOwner = CreateValidManifest() with { OwnerId = null, OrgId = "finance" };
        var ex1 = Assert.Throws<ArgumentException>(() => orgWithoutOwner.Validate());
        Assert.Contains("orgId cannot be set without ownerId", ex1.Message);

        // Invalid Owner pattern
        var invalidOwner = CreateValidManifest() with { OwnerId = "-starts-with-dash" };
        Assert.Throws<ArgumentException>(() => invalidOwner.Validate());

        // Valid ownerless manifest
        var ownerless = CreateValidManifest() with { OwnerId = null, OrgId = null };
        ownerless.Validate();
    }

    [Fact]
    public void ReadyManifestBlob_Validation_FailsWhenNeitherVersionIdNorEtagSpecified()
    {
        var blob = new ReadyManifestBlob("workflow-input", "input.dat", null, null, 1024, SampleSha256);
        var ex = Assert.Throws<ArgumentException>(() => blob.Validate());
        Assert.Contains("versionId or etag", ex.Message, StringComparison.OrdinalIgnoreCase);

        var blobEmpty = new ReadyManifestBlob("workflow-input", "input.dat", " ", "", 1024, SampleSha256);
        var ex2 = Assert.Throws<ArgumentException>(() => blobEmpty.Validate());
        Assert.Contains("versionId or etag", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadyManifestBlob_Validation_SucceedsWithOnlyVersionId()
    {
        var blob = new ReadyManifestBlob("workflow-input", "input.dat", "2026-10-19T00:00:00Z", null, 1024, SampleSha256);
        blob.Validate();
        Assert.NotNull(blob.VersionId);
        Assert.Null(blob.Etag);
    }

    [Fact]
    public void ReadyManifestBlob_Validation_SucceedsWithOnlyEtag()
    {
        var blob = new ReadyManifestBlob("workflow-input", "input.dat", null, "\"0x8D9...\"", 1024, SampleSha256);
        blob.Validate();
        Assert.Null(blob.VersionId);
        Assert.NotNull(blob.Etag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPPERCASE")]
    [InlineData("co")] // too short (min 3)
    [InlineData("has--double-hyphen")]
    [InlineData("-starts-hyphen")]
    [InlineData("ends-hyphen-")]
    public void ReadyManifestBlob_Validation_FailsOnInvalidContainer(string invalidContainer)
    {
        var blob = new ReadyManifestBlob(invalidContainer, "valid/key.dat", "v1", null, 100, SampleSha256);
        var ex = Assert.Throws<ArgumentException>(() => blob.Validate());
        Assert.Contains("container", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/leading/slash.dat")]
    [InlineData("\\leading\\backslash.dat")]
    [InlineData("path/../traversal.dat")]
    [InlineData("windows\\style\\path.dat")]
    public void ReadyManifestBlob_Validation_FailsOnInvalidBlobKeyTraversalOrAbsolute(string invalidKey)
    {
        var blob = new ReadyManifestBlob("valid-container", invalidKey, "v1", null, 100, SampleSha256);
        var ex = Assert.Throws<ArgumentException>(() => blob.Validate());
        Assert.Contains("blobKey", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1, SampleSha256)]
    [InlineData(100, "")]
    [InlineData(100, "01234")] // too short
    [InlineData(100, "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")] // uppercase
    [InlineData(100, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")] // non-hex 'g'
    public void ReadyManifestBlob_Validation_FailsOnInvalidSha256OrSize(long size, string sha)
    {
        var blob = new ReadyManifestBlob("valid-container", "valid/key.dat", "v1", null, size, sha);
        Assert.Throws<ArgumentException>(() => blob.Validate());
    }

    [Fact]
    public void ReadyManifest_JsonSerialization_RoundtripsAndMatchesExpectedSchema()
    {
        var original = CreateValidManifest();
        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("input-manifest-001", root.GetProperty("readyManifestId").GetString());
        Assert.Equal("alice", root.GetProperty("ownerId").GetString());
        Assert.Equal("finance", root.GetProperty("orgId").GetString());
        Assert.True(root.TryGetProperty("blob", out var blobProp));
        Assert.Equal("workflow-input", blobProp.GetProperty("container").GetString());
        Assert.Equal("owners/alice/inputs/batch-01/input.dat", blobProp.GetProperty("blobKey").GetString());
        Assert.Equal(SampleSha256, blobProp.GetProperty("sha256").GetString());

        var deserialized = JsonSerializer.Deserialize<ReadyManifest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(deserialized);
        Assert.Equal(original.ReadyManifestId, deserialized.ReadyManifestId);
        Assert.Equal(original.Blob.Sha256, deserialized.Blob.Sha256);
        Assert.Equal(original.Blob.SizeBytes, deserialized.Blob.SizeBytes);
    }

    [Fact]
    public async Task BlobStorageRepository_SaveAndGet_Succeeds()
    {
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest();
        AddValidTestBlob(repo, manifest);

        var blobStorage = new BlobStorageRepository(repo);

        var uri = await blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);
        Assert.NotNull(uri);

        var retrieved = await blobStorage.GetReadyManifestAsync(manifest.ReadyManifestId, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal(manifest.ReadyManifestId, retrieved.ReadyManifestId);
        Assert.Equal(manifest.Blob.Sha256, retrieved.Blob.Sha256);
    }

    [Fact]
    public async Task BlobStorageRepository_Save_IsIdempotentForIdenticalContent()
    {
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest();
        AddValidTestBlob(repo, manifest);

        var blobStorage = new BlobStorageRepository(repo);

        await blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);
        // Second identical save should succeed idempotently
        var uri = await blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);
        Assert.NotNull(uri);
        Assert.Equal(1, repo.Creates);
    }

    [Fact]
    public async Task BlobStorageRepository_Save_ThrowsConflictForDifferentContent()
    {
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest();
        AddValidTestBlob(repo, manifest);

        var blobStorage = new BlobStorageRepository(repo);

        await blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);

        var modifiedBytes = new byte[9999];
        var modifiedSha = Convert.ToHexString(SHA256.HashData(modifiedBytes)).ToLowerInvariant();
        var modified = manifest with
        {
            Blob = manifest.Blob with { SizeBytes = modifiedBytes.Length, Sha256 = modifiedSha }
        };
        // Add blob for modified so it passes target verification and triggers conflict check
        repo.Blobs[$"{modified.Blob.Container}/{modified.Blob.BlobKey}"] = modifiedBytes;

        await Assert.ThrowsAsync<WorkflowConflictException>(() => blobStorage.SaveReadyManifestAsync(modified, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BlobStorageRepository_Save_ThrowsWhenTargetBlobMissing()
    {
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest();
        var blobStorage = new BlobStorageRepository(repo);

        // Target blob not added to repo.Blobs
        await Assert.ThrowsAsync<FileNotFoundException>(() => blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BlobStorageRepository_Get_ReturnsNullOrThrowsWhenNotFound()
    {
        var repo = new InMemoryResultRepository();
        var blobStorage = new BlobStorageRepository(repo);

        var result = await blobStorage.TryGetReadyManifestAsync("non-existent", TestContext.Current.CancellationToken);
        Assert.Null(result);

        await Assert.ThrowsAsync<FileNotFoundException>(() => blobStorage.GetReadyManifestAsync("non-existent", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("valid-container", "owners/alice/file.dat", true)]
    [InlineData("INVALID_UPPER", "file.dat", false)]
    [InlineData("valid-container", "/absolute/file.dat", false)]
    [InlineData("valid-container", "file/../back.dat", false)]
    [InlineData("valid-container", "win\\path.dat", false)]
    public void BlobStorageRepository_ValidateTargetBlobLocation_RejectsInvalidLocations(string container, string key, bool valid)
    {
        if (valid)
        {
            BlobStorageRepository.ValidateTargetBlobLocation(container, key);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => BlobStorageRepository.ValidateTargetBlobLocation(container, key));
        }
    }

    [Fact]
    public async Task BlobStorageRepository_VerifyBlobContent_SucceedsForMatchingContent()
    {
        var content = "The quick brown fox jumps over the lazy dog";
        var bytes = Encoding.UTF8.GetBytes(content);
        var expectedSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var stream = new MemoryStream(bytes);
        await BlobStorageRepository.VerifyBlobContentAsync(stream, bytes.Length, expectedSha, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BlobStorageRepository_VerifyBlobContent_FailsOnSizeMismatch()
    {
        var content = "test content";
        var bytes = Encoding.UTF8.GetBytes(content);
        var expectedSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var stream = new MemoryStream(bytes);
        // Expecting larger size than actual
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BlobStorageRepository.VerifyBlobContentAsync(stream, bytes.Length + 10, expectedSha, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BlobStorageRepository_VerifyBlobContent_FailsOnSha256Mismatch()
    {
        var content = "test content";
        var bytes = Encoding.UTF8.GetBytes(content);
        var wrongSha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BlobStorageRepository.VerifyBlobContentAsync(stream, bytes.Length, wrongSha, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RequestFingerprint_LegacyRequest_ComputesV1DeterministicHash()
    {
        var legacyRequest = new WorkflowRequest("req-v1-001", "light", 3);
        var fp1 = RequestFingerprint.Compute(legacyRequest, "light");
        var fp2 = RequestFingerprint.Compute(legacyRequest, "light");

        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length);

        // Matches legacy direct compute v1
        var v1Direct = RequestFingerprint.ComputeV1(legacyRequest, "light");
        Assert.Equal(v1Direct, fp1);
    }

    [Fact]
    public void RequestFingerprint_V2RequestWithReadyManifest_ComputesDifferentFromV1()
    {
        var legacyRequest = new WorkflowRequest("req-v2-001", "light", 3);
        var manifest = CreateValidManifest();

        var v1Fingerprint = RequestFingerprint.Compute(legacyRequest, "light");
        var v2FingerprintWithManifest = RequestFingerprint.Compute(legacyRequest with { ReadyManifestId = manifest.ReadyManifestId }, "light", manifest);

        Assert.NotEqual(v1Fingerprint, v2FingerprintWithManifest);
        Assert.Equal(64, v2FingerprintWithManifest.Length);

        // Changes when manifest hash changes
        var manifestModified = manifest with { Blob = manifest.Blob with { Sha256 = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" } };
        var v2FingerprintDifferentHash = RequestFingerprint.Compute(legacyRequest with { ReadyManifestId = manifest.ReadyManifestId }, "light", manifestModified);
        Assert.NotEqual(v2FingerprintWithManifest, v2FingerprintDifferentHash);

        // Changes when algorithmVersion changes
        var v2WithAlgorithm = RequestFingerprint.Compute(legacyRequest with { ReadyManifestId = manifest.ReadyManifestId, AlgorithmVersion = "2.1.0" }, "light", manifest);
        Assert.NotEqual(v2FingerprintWithManifest, v2WithAlgorithm);
    }

    [Fact]
    public async Task StartWorkflow_WithReadyManifest_Valid_Returns202Accepted()
    {
        var (client, entities) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest("manifest-ok", ownerId: "alice", orgId: "finance");
        AddValidTestBlob(repo, manifest);

        var blobStorage = new BlobStorageRepository(repo);
        await blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);

        var admission = new WorkflowAdmission(repo);
        var callerResolver = new PocFunctionKeyCallerResolver(); // supports X-Poc-Owner-Id
        var functions = new WorkflowFunctions(null!, admission, callerResolver);

        var workflowReq = new WorkflowRequest("req-ready-202", ReadyManifestId: "manifest-ok");
        var headers = new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance"
        };
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq), headers: headers);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("instanceId", out var instanceIdProp));
        Assert.Equal(WorkflowIdentity.InstanceId("req-ready-202"), instanceIdProp.GetString());

        var record = await admission.GetRecordAsync("req-ready-202", TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        Assert.Equal("manifest-ok", record.Request.ReadyManifestId);
        Assert.Equal("alice", record.Owner?.OwnerId);
        Assert.Equal("finance", record.Owner?.OrgId);

        entities.Verify(e => e.SignalEntityAsync(
            new EntityInstanceId(nameof(WorkflowAdmissionEntity), WorkflowIdentity.InstanceId("req-ready-202")),
            WorkflowAdmissionEntity.StartOperation, It.IsAny<WorkflowRequest>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartWorkflow_WithReadyManifest_MissingManifest_Returns400BadRequest()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var admission = new WorkflowAdmission(repo);
        var functions = new WorkflowFunctions(null!, admission, new PocFunctionKeyCallerResolver());

        var workflowReq = new WorkflowRequest("req-missing-manifest", ReadyManifestId: "manifest-does-not-exist");
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq));

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Contains("not found", errorProp.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartWorkflow_WithReadyManifest_TargetBlobMissing_Returns400BadRequest()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest("manifest-missing-blob", ownerId: "alice", orgId: "finance");
        // Put manifest into documents directly without adding blob to Blobs
        repo.Documents[WorkflowIdentity.ReadyManifestPath("manifest-missing-blob")] = JsonSerializer.Serialize(manifest);

        var admission = new WorkflowAdmission(repo);
        var callerResolver = new PocFunctionKeyCallerResolver();
        var functions = new WorkflowFunctions(null!, admission, callerResolver);

        var workflowReq = new WorkflowRequest("req-missing-target-blob", ReadyManifestId: "manifest-missing-blob");
        var headers = new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance"
        };
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq), headers: headers);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Contains("target blob", errorProp.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartWorkflow_WithReadyManifest_TargetBlobHashMismatch_Returns400BadRequest()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest("manifest-corrupt-blob", ownerId: "alice", orgId: "finance");
        // Add blob with wrong content (wrong hash)
        repo.Blobs[$"{manifest.Blob.Container}/{manifest.Blob.BlobKey}"] = Encoding.UTF8.GetBytes(new string('z', 1024));
        repo.Documents[WorkflowIdentity.ReadyManifestPath("manifest-corrupt-blob")] = JsonSerializer.Serialize(manifest);

        var admission = new WorkflowAdmission(repo);
        var callerResolver = new PocFunctionKeyCallerResolver();
        var functions = new WorkflowFunctions(null!, admission, callerResolver);

        var workflowReq = new WorkflowRequest("req-corrupt-target-blob", ReadyManifestId: "manifest-corrupt-blob");
        var headers = new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance"
        };
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq), headers: headers);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Contains("target blob", errorProp.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartWorkflow_WithReadyManifest_OwnerMismatch_Returns403Forbidden()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest("manifest-owned", ownerId: "alice", orgId: "finance");
        AddValidTestBlob(repo, manifest);

        var blobStorage = new BlobStorageRepository(repo);
        await blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);

        var admission = new WorkflowAdmission(repo);
        var callerResolver = new PocFunctionKeyCallerResolver();
        var functions = new WorkflowFunctions(null!, admission, callerResolver);

        var workflowReq = new WorkflowRequest("req-owner-mismatch", ReadyManifestId: "manifest-owned");
        // Caller claims to be 'bob'
        var headers = new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "bob",
            ["X-Poc-Org-Id"] = "finance"
        };
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq), headers: headers);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonBodyAsync(response, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Contains("not authorized", errorProp.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartWorkflow_WithReadyManifest_UnauthenticatedCaller_Returns403Forbidden()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest("manifest-needs-auth", ownerId: "alice", orgId: "finance");
        AddValidTestBlob(repo, manifest);

        var blobStorage = new BlobStorageRepository(repo);
        await blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);

        var admission = new WorkflowAdmission(repo);
        var callerResolver = new PocFunctionKeyCallerResolver();
        var functions = new WorkflowFunctions(null!, admission, callerResolver);

        var workflowReq = new WorkflowRequest("req-unauth", ReadyManifestId: "manifest-needs-auth");
        // Caller has no owner headers
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq));

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StartWorkflow_WithReadyManifest_DuplicateSubmission_IsIdempotent202()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest("manifest-dup", ownerId: "alice", orgId: "finance");
        AddValidTestBlob(repo, manifest);

        var blobStorage = new BlobStorageRepository(repo);
        await blobStorage.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);

        var admission = new WorkflowAdmission(repo);
        var callerResolver = new PocFunctionKeyCallerResolver();
        var functions = new WorkflowFunctions(null!, admission, callerResolver);

        var workflowReq = new WorkflowRequest("req-duplicate-ready", ReadyManifestId: "manifest-dup");
        var headers = new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance"
        };

        var request1 = CreateRequest(JsonSerializer.Serialize(workflowReq), headers: headers);
        var response1 = await functions.StartWorkflow(request1, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response1.StatusCode);

        var request2 = CreateRequest(JsonSerializer.Serialize(workflowReq), headers: headers);
        var response2 = await functions.StartWorkflow(request2, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response2.StatusCode);
    }

    [Fact]
    public async Task StartWorkflow_WithReadyManifest_ConflictingSubmission_Returns409Conflict()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var manifest1 = CreateValidManifest("manifest-conf-1", ownerId: "alice", orgId: "finance");
        var manifest2 = CreateValidManifest("manifest-conf-2", ownerId: "alice", orgId: "finance");
        AddValidTestBlob(repo, manifest1);
        AddValidTestBlob(repo, manifest2);

        var blobStorage = new BlobStorageRepository(repo);
        await blobStorage.SaveReadyManifestAsync(manifest1, TestContext.Current.CancellationToken);
        await blobStorage.SaveReadyManifestAsync(manifest2, TestContext.Current.CancellationToken);

        var admission = new WorkflowAdmission(repo);
        var callerResolver = new PocFunctionKeyCallerResolver();
        var functions = new WorkflowFunctions(null!, admission, callerResolver);

        var headers = new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance"
        };

        // First submission with manifest-conf-1
        var workflowReq1 = new WorkflowRequest("req-conflict-manifest", ReadyManifestId: "manifest-conf-1");
        var request1 = CreateRequest(JsonSerializer.Serialize(workflowReq1), headers: headers);
        var response1 = await functions.StartWorkflow(request1, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response1.StatusCode);

        // Second submission with same requestId but manifest-conf-2
        var workflowReq2 = new WorkflowRequest("req-conflict-manifest", ReadyManifestId: "manifest-conf-2");
        var request2 = CreateRequest(JsonSerializer.Serialize(workflowReq2), headers: headers);
        var response2 = await functions.StartWorkflow(request2, client.Object, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);

        var body = await ReadJsonBodyAsync(response2, TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("error", out var errorProp));
        Assert.Contains("already has different configuration", errorProp.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartWorkflow_LegacyRequest_WithoutReadyManifest_Succeeds202()
    {
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var admission = new WorkflowAdmission(repo);
        var functions = new WorkflowFunctions(null!, admission, new PocFunctionKeyCallerResolver());

        var legacyReq = new WorkflowRequest("req-legacy-202");
        var request = CreateRequest(JsonSerializer.Serialize(legacyReq));

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var record = await admission.GetRecordAsync("req-legacy-202", TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        Assert.Null(record.Request.ReadyManifestId);
    }

    [Fact]
    public async Task StartWorkflow_LegacyAdmissionJsonV2_ResendSucceedsWith202()
    {
        // Regression test for P2 issue 1:
        // Existing admission.json v2 saved with legacy schema (without newer optional fields)
        // must be accepted idempotently on retry without throwing 500.
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-legacy-resend-001";
        var instanceId = WorkflowIdentity.InstanceId(requestId);

        // Raw legacy admission.json v2 as originally stored by earlier codebase
        var legacyRawJson = $"{{\"schemaVersion\":2,\"instanceId\":\"{instanceId}\",\"request\":{{\"requestId\":\"{requestId}\",\"classification\":\"auto\",\"taskCount\":3,\"failTaskIndex\":null,\"lightDelaySeconds\":5,\"heavyDelaySeconds\":15}},\"owner\":null}}";
        repo.Documents[WorkflowIdentity.AdmissionPath(requestId)] = legacyRawJson;

        var admission = new WorkflowAdmission(repo);
        var callerResolver = new PocFunctionKeyCallerResolver();
        var functions = new WorkflowFunctions(null!, admission, callerResolver);

        // Resend identical request
        var workflowReq = new WorkflowRequest(requestId, "auto", 3);
        var request = CreateRequest(JsonSerializer.Serialize(workflowReq));

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task AdmitAsync_LegacyAdmissionJsonV2_ResendSucceedsWithout500()
    {
        // Regression test for P2 issue 1:
        // Direct AdmitAsync call against existing legacy admission.json v2 must return instanceId idempotently
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var requestId = "req-legacy-admit-002";
        var instanceId = WorkflowIdentity.InstanceId(requestId);

        var legacyRawJson = $"{{\"schemaVersion\":2,\"instanceId\":\"{instanceId}\",\"request\":{{\"requestId\":\"{requestId}\",\"classification\":\"auto\",\"taskCount\":3,\"failTaskIndex\":null,\"lightDelaySeconds\":5,\"heavyDelaySeconds\":15}},\"owner\":null}}";
        repo.Documents[WorkflowIdentity.AdmissionPath(requestId)] = legacyRawJson;

        var admission = new WorkflowAdmission(repo);
        var input = new WorkflowRequest(requestId, "auto", 3);

        var admittedInstanceId = await admission.AdmitAsync(input, client.Object, null, TestContext.Current.CancellationToken);
        Assert.Equal(instanceId, admittedInstanceId);
    }

    [Fact]
    public async Task StartWorkflow_ReadyManifest_TransientStorageFailure_Returns503WithRetryAfter()
    {
        // Regression test for Issue 1:
        // Transient storage failure when fetching ReadyManifest must return 503 Service Unavailable with Retry-After
        var (client, _) = CreateDurableClient();
        var repo = new Mock<IResultRepository>();
        repo.Setup(r => r.ReadAsync<ReadyManifest>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Azure.RequestFailedException(503, "Storage is temporarily unavailable."));

        var admission = new WorkflowAdmission(repo.Object);
        var operations = new WorkflowOperations(new FakeBatchGateway(), repo.Object, new StubWorkloadClassifier(), new WorkflowSettings());
        var functions = new WorkflowFunctions(operations, admission, new PocFunctionKeyCallerResolver());

        var requestJson = JsonSerializer.Serialize(new
        {
            requestId = "req-transient-manifest-001",
            readyManifestId = "manifest-transient-001"
        });
        var request = CreateRequest(requestJson);

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Equal("10", response.Headers.GetValues("Retry-After").First());
    }

    [Fact]
    public async Task StartWorkflow_ReadyManifestBlobVerification_TransientStorageFailure_Returns503WithRetryAfter()
    {
        // Regression test for Issue 1:
        // Transient storage failure when verifying ReadyManifest target blob must return 503 Service Unavailable with Retry-After
        var (client, _) = CreateDurableClient();
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest("manifest-transient-002");
        await repo.PutIfAbsentAsync(WorkflowIdentity.ReadyManifestPath(manifest.ReadyManifestId), manifest, TestContext.Current.CancellationToken);

        var storageRepo = new BlobStorageRepository(repo)
        {
            TargetBlobVerifier = (_, _) => throw new Azure.RequestFailedException(503, "Storage transient failure during stream download.")
        };
        var admission = new WorkflowAdmission(repo, storageRepo);
        var operations = new WorkflowOperations(new FakeBatchGateway(), repo, new StubWorkloadClassifier(), new WorkflowSettings(), storageRepo);
        var functions = new WorkflowFunctions(operations, admission, new PocFunctionKeyCallerResolver());

        var requestJson = JsonSerializer.Serialize(new
        {
            requestId = "req-transient-blob-002",
            readyManifestId = manifest.ReadyManifestId
        });
        var request = CreateRequest(requestJson, headers: new Dictionary<string, string>
        {
            ["X-Poc-Owner-Id"] = "alice",
            ["X-Poc-Org-Id"] = "finance"
        });

        var response = await functions.StartWorkflow(request, client.Object, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        Assert.Equal("10", response.Headers.GetValues("Retry-After").First());
    }

    [Fact]
    public async Task WorkflowOperations_SaveReadyManifestAsync_WithInjectedRepository_Succeeds()
    {
        // Regression test for Issue 2:
        // WorkflowOperations with DI-injected BlobStorageRepository must use the injected repository
        // and successfully verify/save without InvalidOperationException
        var repo = new InMemoryResultRepository();
        var manifest = CreateValidManifest("manifest-ops-001");
        AddValidTestBlob(repo, manifest);

        bool verifierInvoked = false;
        var storageRepo = new BlobStorageRepository(repo)
        {
            TargetBlobVerifier = (m, ct) =>
            {
                verifierInvoked = true;
                return Task.CompletedTask;
            }
        };

        var operations = new WorkflowOperations(new FakeBatchGateway(), repo, new StubWorkloadClassifier(), new WorkflowSettings(), storageRepo);
        var savedPath = await operations.SaveReadyManifestAsync(manifest, TestContext.Current.CancellationToken);

        Assert.True(verifierInvoked, "TargetBlobVerifier on injected BlobStorageRepository should have been called.");
        Assert.NotNull(savedPath);
        Assert.Contains(WorkflowIdentity.ReadyManifestPath(manifest.ReadyManifestId), savedPath);

        var retrieved = await operations.GetReadyManifestAsync(manifest.ReadyManifestId, TestContext.Current.CancellationToken);
        Assert.NotNull(retrieved);
        Assert.Equal(manifest.ReadyManifestId, retrieved.ReadyManifestId);
    }

    private static (Mock<DurableTaskClient> Client, Mock<DurableEntityClient> Entities) CreateDurableClient()
    {
        var client = new Mock<DurableTaskClient>("test");
        var entities = new Mock<DurableEntityClient>("test");
        client.SetupGet(c => c.Entities).Returns(entities.Object);
        client.Setup(c => c.GetInstanceAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);
        client.Setup(c => c.GetInstanceAsync(It.IsAny<string>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrchestrationMetadata?)null);
        entities.Setup(e => e.SignalEntityAsync(It.IsAny<EntityInstanceId>(), It.IsAny<string>(),
            It.IsAny<object>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return (client, entities);
    }

    private static FakeHttpRequestData CreateRequest(string body, string url = "https://localhost:7071/api/workflows", string method = "POST", IDictionary<string, string>? headers = null)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(options =>
        {
            options.Serializer = new Azure.Core.Serialization.JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        var sp = services.BuildServiceProvider();

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.InstanceServices).Returns(sp);

        var request = new FakeHttpRequestData(context.Object, body, new Uri(url), method);
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                request.Headers.Add(k, v);
            }
        }
        return request;
    }

    private static async Task<JsonElement> ReadJsonBodyAsync(HttpResponseData response, CancellationToken cancellationToken)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(cancellationToken);
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.Clone();
    }
}
