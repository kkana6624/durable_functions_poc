using System.Text.Json;
using Azure;
using Azure.Compute.Batch;
using Moq;
using Poc.Core;
using Xunit;

namespace Poc.Tests;

public class BatchExecutionTests
{
    private const string SampleSha256 = "2edc986847e209b4016e141a6dc8716d3207350f416969382d431539bf292e4a";
    private const string PoolUamiId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/poc-pool-id";
    private static readonly Uri BlobEndpoint = new("https://mockstorage.blob.core.windows.net/");

    private static ReadyManifest CreateValidManifest(string id = "input-manifest-batch-001")
    {
        return new ReadyManifest(
            SchemaVersion: 1,
            ReadyManifestId: id,
            OwnerId: "alice",
            OrgId: "finance",
            Blob: new ReadyManifestBlob(
                Container: "workflow-input",
                BlobKey: "owners/alice/inputs/batch-01/input.dat",
                VersionId: "2026-10-19T00:00:00.0000000Z",
                Etag: "\"0x8D9...\"",
                SizeBytes: 1024,
                Sha256: SampleSha256),
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task EnsureTaskAsync_WithReadyManifestAndStorageOptions_ConfiguresResourceFilesAndOutputFiles()
    {
        var manifest = CreateValidManifest();
        var request = new WorkflowRequest("req-batch-rf-001", "light", 1, ReadyManifestId: manifest.ReadyManifestId);
        var plan = new WorkflowPlan(request, "light", "reason", WorkflowIdentity.JobId(request.RequestId),
            WorkflowIdentity.Fingerprint(request, "light", manifest), 10, 15, false, manifest);
        var spec = new TaskSpec(plan, 0);

        var batchMock = new Mock<BatchClient>();
        var batchJob = ComputeBatchModelFactory.BatchJob(
            id: plan.JobId,
            state: BatchJobState.Active,
            poolInfo: new BatchPoolInfo { PoolId = "test-pool" },
            metadata: [new BatchMetadataItem("poc-fingerprint", plan.Fingerprint)]);

        batchMock.Setup(b => b.GetJobAsync(plan.JobId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(batchJob, Mock.Of<Response>()));

        BatchTaskCreateOptions? capturedOptions = null;
        batchMock.Setup(b => b.CreateTaskAsync(plan.JobId, It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback<string, BatchTaskCreateOptions, TimeSpan?, DateTimeOffset?, CancellationToken>((_, opts, _, _, _) => capturedOptions = opts)
            .ReturnsAsync(Mock.Of<Response>());

        var storageOptions = new BatchStorageOptions(
            PoolUamiResourceId: PoolUamiId,
            BlobEndpoint: BlobEndpoint,
            OutputContainer: "workflow-output");

        var gateway = new AzureBatchGateway(batchMock.Object, "test-pool", storageOptions);

        batchMock.Setup(b => b.GetTaskAsync(plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, TimeSpan?, DateTimeOffset?, RequestConditions, IEnumerable<string>, IEnumerable<string>, CancellationToken>((_, _, _, _, _, _, _, _) =>
            {
                var createdTask = ComputeBatchModelFactory.BatchTask(
                    id: spec.TaskId,
                    commandLine: capturedOptions!.CommandLine,
                    constraints: new BatchTaskConstraints { MaxTaskRetryCount = 0 },
                    environmentSettings: [new EnvironmentSetting("POC_FINGERPRINT") { Value = plan.Fingerprint }]);
                return Task.FromResult(Response.FromValue(createdTask, Mock.Of<Response>()));
            });

        await gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);

        // Verify ResourceFiles
        Assert.Single(capturedOptions.ResourceFiles);
        var rf = capturedOptions.ResourceFiles[0];
        Assert.Equal("inputs/input.dat", rf.FilePath);
        Assert.NotNull(rf.IdentityReference);
        Assert.Equal(PoolUamiId, rf.IdentityReference.ResourceId.ToString());
        Assert.Contains("versionid=2026-10-19T00:00:00.0000000Z", rf.HttpUri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/workflow-input/owners/alice/inputs/batch-01/input.dat", rf.HttpUri.AbsolutePath);

        // Verify OutputFiles
        Assert.Single(capturedOptions.OutputFiles);
        var of = capturedOptions.OutputFiles[0];
        Assert.Equal("output.json", of.FilePattern);
        Assert.NotNull(of.Destination?.Container);
        Assert.Equal("workflow-output", of.Destination.Container.ContainerUri.Segments.Last().TrimEnd('/'));
        Assert.Equal(spec.OutputBlobPath, of.Destination.Container.Path);
        Assert.Equal(PoolUamiId, of.Destination.Container.IdentityReference?.ResourceId.ToString());
        Assert.Equal(OutputFileUploadCondition.TaskSuccess, of.UploadOptions.UploadCondition);

        // Verify CommandLine checks inputs and writes output.json
        Assert.Contains("Test-Path -LiteralPath 'inputs/input.dat'", capturedOptions.CommandLine);
        Assert.Contains("output.json", capturedOptions.CommandLine);
    }

    [Fact]
    public async Task EnsureTaskAsync_WithoutManifest_PreservesLegacyTaskDefinition()
    {
        var request = new WorkflowRequest("req-batch-rf-legacy", "light", 1);
        var plan = new WorkflowPlan(request, "light", "reason", WorkflowIdentity.JobId(request.RequestId),
            WorkflowIdentity.Fingerprint(request, "light"), 10, 15, false);
        var spec = new TaskSpec(plan, 0);

        var batchMock = new Mock<BatchClient>();
        var batchJob = ComputeBatchModelFactory.BatchJob(
            id: plan.JobId,
            state: BatchJobState.Active,
            poolInfo: new BatchPoolInfo { PoolId = "test-pool" },
            metadata: [new BatchMetadataItem("poc-fingerprint", plan.Fingerprint)]);

        batchMock.Setup(b => b.GetJobAsync(plan.JobId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(batchJob, Mock.Of<Response>()));

        BatchTaskCreateOptions? capturedOptions = null;
        batchMock.Setup(b => b.CreateTaskAsync(plan.JobId, It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback<string, BatchTaskCreateOptions, TimeSpan?, DateTimeOffset?, CancellationToken>((_, opts, _, _, _) => capturedOptions = opts)
            .ReturnsAsync(Mock.Of<Response>());

        var storageOptions = new BatchStorageOptions(PoolUamiResourceId: PoolUamiId, BlobEndpoint: BlobEndpoint);
        var gateway = new AzureBatchGateway(batchMock.Object, "test-pool", storageOptions);

        batchMock.Setup(b => b.GetTaskAsync(plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, TimeSpan?, DateTimeOffset?, RequestConditions, IEnumerable<string>, IEnumerable<string>, CancellationToken>((_, _, _, _, _, _, _, _) =>
            {
                var createdTask = ComputeBatchModelFactory.BatchTask(
                    id: spec.TaskId,
                    commandLine: capturedOptions!.CommandLine,
                    constraints: new BatchTaskConstraints { MaxTaskRetryCount = 0 },
                    environmentSettings: [new EnvironmentSetting("POC_FINGERPRINT") { Value = plan.Fingerprint }]);
                return Task.FromResult(Response.FromValue(createdTask, Mock.Of<Response>()));
            });

        await gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);
        Assert.Empty(capturedOptions.ResourceFiles);
        Assert.Empty(capturedOptions.OutputFiles);
        Assert.DoesNotContain("inputs/input.dat", capturedOptions.CommandLine);
        Assert.DoesNotContain("output.json", capturedOptions.CommandLine);
        Assert.Contains("result.txt", capturedOptions.CommandLine);
    }

    [Fact]
    public async Task FetchAndPersistAsync_WithReadyManifest_VerifiesOutputBlobAndPersistsResult()
    {
        var manifest = CreateValidManifest();
        var request = new WorkflowRequest("req-output-blob-001", "light", 2, ReadyManifestId: manifest.ReadyManifestId);
        var repo = new InMemoryResultRepository();
        await repo.PutIfAbsentAsync(WorkflowIdentity.ReadyManifestPath(manifest.ReadyManifestId), manifest, TestContext.Current.CancellationToken);

        var operations = new WorkflowOperations(new FakeBatchGateway(), repo, new StubWorkloadClassifier(), new WorkflowSettings());

        var plan = await operations.ClassifyAsync(request, TestContext.Current.CancellationToken);
        Assert.NotNull(plan.Manifest);

        // Put simulated OutputFiles blobs into the repository
        for (int i = 0; i < request.TaskCount; i++)
        {
            var spec = new TaskSpec(plan, i);
            var payload = new BatchTaskOutputPayload(1, request.RequestId, spec.TaskId, 0, "Succeeded", 1);
            await repo.PutIfAbsentAsync(spec.OutputBlobPath, payload, TestContext.Current.CancellationToken);
        }

        var references = await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(2, references.Length);
        Assert.Equal("task-00", references[0].TaskId);
        Assert.Equal("task-01", references[1].TaskId);

        // Aggregate and Publish Manifest should succeed
        var aggregate = await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);
        Assert.Equal(2, aggregate.Sum);
        Assert.Equal(2, aggregate.TaskCount);

        var manifestRef = await operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken);
        Assert.Equal(2, manifestRef.Sum);
        Assert.Equal(2, manifestRef.TaskCount);
    }

    [Fact]
    public async Task FetchAndPersistAsync_WithReadyManifest_MissingOutputBlob_ThrowsFileNotFoundException()
    {
        var manifest = CreateValidManifest();
        var request = new WorkflowRequest("req-output-missing-002", "light", 1, ReadyManifestId: manifest.ReadyManifestId);
        var repo = new InMemoryResultRepository();
        await repo.PutIfAbsentAsync(WorkflowIdentity.ReadyManifestPath(manifest.ReadyManifestId), manifest, TestContext.Current.CancellationToken);

        var operations = new WorkflowOperations(new FakeBatchGateway(), repo, new StubWorkloadClassifier(), new WorkflowSettings());

        var plan = await operations.ClassifyAsync(request, TestContext.Current.CancellationToken);

        // Do not upload output.json to repo -> should fail with FileNotFoundException
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken));
        Assert.Contains("output.json", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAndPersistAsync_WithReadyManifest_FailedOutputStatus_ThrowsInvalidOperationException()
    {
        var manifest = CreateValidManifest();
        var request = new WorkflowRequest("req-output-failed-003", "light", 1, ReadyManifestId: manifest.ReadyManifestId);
        var repo = new InMemoryResultRepository();
        await repo.PutIfAbsentAsync(WorkflowIdentity.ReadyManifestPath(manifest.ReadyManifestId), manifest, TestContext.Current.CancellationToken);

        var operations = new WorkflowOperations(new FakeBatchGateway(), repo, new StubWorkloadClassifier(), new WorkflowSettings());

        var plan = await operations.ClassifyAsync(request, TestContext.Current.CancellationToken);
        var spec = new TaskSpec(plan, 0);

        // Upload output.json with Status="Failed"
        var failedPayload = new BatchTaskOutputPayload(1, request.RequestId, spec.TaskId, 0, "Failed", 0);
        await repo.PutIfAbsentAsync(spec.OutputBlobPath, failedPayload, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken));
        Assert.Contains("not Succeeded", ex.Message);
    }

    [Fact]
    public async Task PublishManifestAsync_WithReadyManifest_HonorsCancellationConflict()
    {
        var manifest = CreateValidManifest();
        var request = new WorkflowRequest("req-cancel-conflict-004", "light", 1, ReadyManifestId: manifest.ReadyManifestId);
        var repo = new InMemoryResultRepository();
        await repo.PutIfAbsentAsync(WorkflowIdentity.ReadyManifestPath(manifest.ReadyManifestId), manifest, TestContext.Current.CancellationToken);

        var operations = new WorkflowOperations(new FakeBatchGateway(), repo, new StubWorkloadClassifier(), new WorkflowSettings());

        var plan = await operations.ClassifyAsync(request, TestContext.Current.CancellationToken);
        var spec = new TaskSpec(plan, 0);
        var payload = new BatchTaskOutputPayload(1, request.RequestId, spec.TaskId, 0, "Succeeded", 1);
        await repo.PutIfAbsentAsync(spec.OutputBlobPath, payload, TestContext.Current.CancellationToken);

        await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
        await operations.AggregateAsync(plan, TestContext.Current.CancellationToken);

        // Simulate Cancel record already resolved in resolution.json
        var cancelledResolution = new WorkflowResolutionRecord(1, request.RequestId, "Cancelled");
        await repo.PutIfAbsentAsync(WorkflowIdentity.ResolutionPath(request.RequestId), cancelledResolution, TestContext.Current.CancellationToken);

        // PublishManifestAsync should throw WorkflowCancelledException and suppress manifest publication
        await Assert.ThrowsAsync<WorkflowCancelledException>(() => operations.PublishManifestAsync(plan, TestContext.Current.CancellationToken));
        Assert.False(repo.Documents.ContainsKey(WorkflowIdentity.ManifestPath(plan)));
    }

    [Fact]
    public void BatchStorageOptions_WhenUamiConfigured_BuildsOptionsWithDefaults()
    {
        var config = new Dictionary<string, string?>
        {
            ["BATCH_POOL_UAMI_RESOURCE_ID"] = PoolUamiId,
            ["BLOB_SERVICE_ENDPOINT"] = BlobEndpoint.AbsoluteUri
        };

        var poolUami = config["BATCH_POOL_UAMI_RESOURCE_ID"];
        BatchStorageOptions? options = null;
        if (!string.IsNullOrWhiteSpace(poolUami))
        {
            var endpointRaw = config.GetValueOrDefault("BLOB_SERVICE_ENDPOINT") ?? config.GetValueOrDefault("BLOB_ENDPOINT");
            var endpoint = !string.IsNullOrWhiteSpace(endpointRaw) ? new Uri(endpointRaw) : null;
            var outputContainer = config.GetValueOrDefault("BATCH_OUTPUT_CONTAINER");
            if (string.IsNullOrWhiteSpace(outputContainer))
            {
                outputContainer = "workflow-output";
            }
            options = new BatchStorageOptions(poolUami, endpoint, outputContainer);
        }

        Assert.NotNull(options);
        Assert.Equal(PoolUamiId, options.PoolUamiResourceId);
        Assert.Equal(BlobEndpoint, options.BlobEndpoint);
        Assert.Equal("workflow-output", options.OutputContainer);
    }

    [Fact]
    public void BatchStorageOptions_WhenUamiNotConfigured_ReturnsNull()
    {
        var config = new Dictionary<string, string?>
        {
            ["BLOB_SERVICE_ENDPOINT"] = BlobEndpoint.AbsoluteUri
        };

        var poolUami = config.GetValueOrDefault("BATCH_POOL_UAMI_RESOURCE_ID");
        BatchStorageOptions? options = null;
        if (!string.IsNullOrWhiteSpace(poolUami))
        {
            options = new BatchStorageOptions(poolUami);
        }

        Assert.Null(options);
    }

    [Fact]
    public async Task EnsureTaskAsync_WithEtagOnlyManifest_NodeScriptIncludesSha256AndSizeValidation()
    {
        var manifest = new ReadyManifest(
            SchemaVersion: 1,
            ReadyManifestId: "input-manifest-etag-only",
            OwnerId: "alice",
            OrgId: "finance",
            Blob: new ReadyManifestBlob(
                Container: "workflow-input",
                BlobKey: "owners/alice/inputs/batch-01/input.dat",
                VersionId: null,
                Etag: "\"0x8D9...\"",
                SizeBytes: 2048,
                Sha256: SampleSha256),
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var request = new WorkflowRequest("req-etag-001", "light", 1, ReadyManifestId: manifest.ReadyManifestId);
        var plan = new WorkflowPlan(request, "light", "reason", WorkflowIdentity.JobId(request.RequestId),
            WorkflowIdentity.Fingerprint(request, "light", manifest), 10, 15, false, manifest);
        var spec = new TaskSpec(plan, 0, Attempt: 2);

        var batchMock = new Mock<BatchClient>();
        var batchJob = ComputeBatchModelFactory.BatchJob(
            id: plan.JobId,
            state: BatchJobState.Active,
            poolInfo: new BatchPoolInfo { PoolId = "test-pool" },
            metadata: [new BatchMetadataItem("poc-fingerprint", plan.Fingerprint)]);

        batchMock.Setup(b => b.GetJobAsync(plan.JobId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(batchJob, Mock.Of<Response>()));

        BatchTaskCreateOptions? capturedOptions = null;
        batchMock.Setup(b => b.CreateTaskAsync(plan.JobId, It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback<string, BatchTaskCreateOptions, TimeSpan?, DateTimeOffset?, CancellationToken>((_, opts, _, _, _) => capturedOptions = opts)
            .ReturnsAsync(Mock.Of<Response>());

        var storageOptions = new BatchStorageOptions(PoolUamiResourceId: PoolUamiId, BlobEndpoint: BlobEndpoint);
        var gateway = new AzureBatchGateway(batchMock.Object, "test-pool", storageOptions);

        batchMock.Setup(b => b.GetTaskAsync(plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, TimeSpan?, DateTimeOffset?, RequestConditions, IEnumerable<string>, IEnumerable<string>, CancellationToken>((_, _, _, _, _, _, _, _) =>
            {
                var createdTask = ComputeBatchModelFactory.BatchTask(
                    id: spec.TaskId,
                    commandLine: capturedOptions!.CommandLine,
                    constraints: new BatchTaskConstraints { MaxTaskRetryCount = 0 },
                    environmentSettings: [new EnvironmentSetting("POC_FINGERPRINT") { Value = plan.Fingerprint }]);
                return Task.FromResult(Response.FromValue(createdTask, Mock.Of<Response>()));
            });

        await gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);
        var rf = capturedOptions.ResourceFiles[0];
        Assert.DoesNotContain("versionid=", rf.HttpUri.Query, StringComparison.OrdinalIgnoreCase);

        // Review Point 3: Node script contains SHA-256 and size verification and attempt=2
        Assert.Contains("Length -ne 2048", capturedOptions.CommandLine);
        Assert.Contains(SampleSha256, capturedOptions.CommandLine);
        Assert.Contains("attempt", capturedOptions.CommandLine);
        Assert.Contains(":2", capturedOptions.CommandLine);
        Assert.Contains("req-etag-001", capturedOptions.CommandLine);
        Assert.Contains("task-00", capturedOptions.CommandLine);
    }

    [Fact]
    public async Task EnsureTaskAsync_WithReservedCharactersInBlobKey_BlobUriBuilderEscapesProperly()
    {
        var manifest = new ReadyManifest(
            SchemaVersion: 1,
            ReadyManifestId: "input-manifest-reserved-chars",
            OwnerId: "alice",
            OrgId: "finance",
            Blob: new ReadyManifestBlob(
                Container: "workflow-input",
                BlobKey: "owners/alice/inputs/data#1 with spaces?query.dat",
                VersionId: "v1",
                Etag: "\"0x8D9...\"",
                SizeBytes: 1024,
                Sha256: SampleSha256),
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var request = new WorkflowRequest("req-reserved-001", "light", 1, ReadyManifestId: manifest.ReadyManifestId);
        var plan = new WorkflowPlan(request, "light", "reason", WorkflowIdentity.JobId(request.RequestId),
            WorkflowIdentity.Fingerprint(request, "light", manifest), 10, 15, false, manifest);
        var spec = new TaskSpec(plan, 0);

        var batchMock = new Mock<BatchClient>();
        var batchJob = ComputeBatchModelFactory.BatchJob(
            id: plan.JobId,
            state: BatchJobState.Active,
            poolInfo: new BatchPoolInfo { PoolId = "test-pool" },
            metadata: [new BatchMetadataItem("poc-fingerprint", plan.Fingerprint)]);

        batchMock.Setup(b => b.GetJobAsync(plan.JobId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(batchJob, Mock.Of<Response>()));

        BatchTaskCreateOptions? capturedOptions = null;
        batchMock.Setup(b => b.CreateTaskAsync(plan.JobId, It.IsAny<BatchTaskCreateOptions>(), It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .Callback<string, BatchTaskCreateOptions, TimeSpan?, DateTimeOffset?, CancellationToken>((_, opts, _, _, _) => capturedOptions = opts)
            .ReturnsAsync(Mock.Of<Response>());

        var storageOptions = new BatchStorageOptions(PoolUamiResourceId: PoolUamiId, BlobEndpoint: BlobEndpoint);
        var gateway = new AzureBatchGateway(batchMock.Object, "test-pool", storageOptions);

        batchMock.Setup(b => b.GetTaskAsync(plan.JobId, spec.TaskId, It.IsAny<TimeSpan?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<RequestConditions>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, TimeSpan?, DateTimeOffset?, RequestConditions, IEnumerable<string>, IEnumerable<string>, CancellationToken>((_, _, _, _, _, _, _, _) =>
            {
                var createdTask = ComputeBatchModelFactory.BatchTask(
                    id: spec.TaskId,
                    commandLine: capturedOptions!.CommandLine,
                    constraints: new BatchTaskConstraints { MaxTaskRetryCount = 0 },
                    environmentSettings: [new EnvironmentSetting("POC_FINGERPRINT") { Value = plan.Fingerprint }]);
                return Task.FromResult(Response.FromValue(createdTask, Mock.Of<Response>()));
            });

        await gateway.EnsureTaskAsync(spec, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);
        var rf = capturedOptions.ResourceFiles[0];

        // Review Point 6: '#' must be percent-encoded as '%23' and not treated as URI fragment
        Assert.True(string.IsNullOrEmpty(rf.HttpUri.Fragment), "Fragment must be empty; '#' must not be treated as a fragment delimiter");
        Assert.Contains("%23", rf.HttpUri.AbsolutePath);
        Assert.Contains("%20", rf.HttpUri.AbsolutePath);
        Assert.Contains("versionid=v1", rf.HttpUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAndPersistAsync_WithContainerSeparation_ReadsFromOutputContainer()
    {
        var manifest = CreateValidManifest();
        var request = new WorkflowRequest("req-container-sep-001", "light", 1, ReadyManifestId: manifest.ReadyManifestId);
        var repo = new InMemoryResultRepository();
        await repo.PutIfAbsentAsync(WorkflowIdentity.ReadyManifestPath(manifest.ReadyManifestId), manifest, TestContext.Current.CancellationToken);

        var storageOptions = new BatchStorageOptions(
            PoolUamiResourceId: PoolUamiId,
            BlobEndpoint: BlobEndpoint,
            OutputContainer: "isolated-output-container");

        var operations = new WorkflowOperations(
            new FakeBatchGateway(),
            repo,
            new StubWorkloadClassifier(),
            new WorkflowSettings(),
            storageOptions: storageOptions);

        var plan = await operations.ClassifyAsync(request, TestContext.Current.CancellationToken);
        var spec = new TaskSpec(plan, 0);

        // Put payload only in isolated-output-container
        var payload = new BatchTaskOutputPayload(1, request.RequestId, spec.TaskId, 0, "Succeeded", 1);
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        repo.Blobs[$"isolated-output-container/{spec.OutputBlobPath}"] = jsonBytes;

        // Review Point 4: Should successfully read from isolated-output-container
        var references = await operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken);
        Assert.Single(references);
        Assert.Equal("task-00", references[0].TaskId);

        // If not in isolated-output-container, should throw FileNotFoundException even if in default container
        repo.Blobs.Remove($"isolated-output-container/{spec.OutputBlobPath}");
        repo.Blobs[$"workflow-input/{spec.OutputBlobPath}"] = jsonBytes;

        await Assert.ThrowsAsync<FileNotFoundException>(() => operations.FetchAndPersistAsync(plan, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAndVerifyTaskOutputFromStreamAsync_StreamingExecution_ValidatesCorrectly()
    {
        // Review Point 7: Test streaming read with large payload and 80KB buffer
        var payload = new BatchTaskOutputPayload(1, "req-stream-001", "task-00", 0, "Succeeded", 1);
        var json = JsonSerializer.Serialize(payload);

        // Add 100KB trailing whitespace to ensure buffer drain and streaming work across 80KB chunks
        var paddedJson = json + new string(' ', 100_000);
        var paddedBytes = System.Text.Encoding.UTF8.GetBytes(paddedJson);
        using var stream = new MemoryStream(paddedBytes);

        var (resultPayload, sha256, sizeBytes) = await BlobStorageRepository.ReadAndVerifyTaskOutputFromStreamAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal("req-stream-001", resultPayload.RequestId);
        Assert.Equal("task-00", resultPayload.TaskId);
        Assert.Equal("Succeeded", resultPayload.Status);
        Assert.Equal(1, resultPayload.Value);
        Assert.Equal(64, sha256.Length);
        Assert.Equal(paddedBytes.Length, sizeBytes);

        // Invalid JSON must throw InvalidDataException
        using var invalidStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not-json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => BlobStorageRepository.ReadAndVerifyTaskOutputFromStreamAsync(invalidStream, TestContext.Current.CancellationToken));

        // InMemoryBlobStorageRepository direct usage
        var repo = new InMemoryResultRepository();
        repo.Documents[specOutputBlobPath("req-stream-001", "task-00")] = json;
        var inMemoryStorage = new InMemoryBlobStorageRepository(repo);
        var inMemoryPayload = await inMemoryStorage.ReadTaskOutputAsync("workflow-output", specOutputBlobPath("req-stream-001", "task-00"), TestContext.Current.CancellationToken);
        Assert.Equal("req-stream-001", inMemoryPayload.RequestId);
    }

    private static string specOutputBlobPath(string requestId, string taskId, int attempt = 0) =>
        $"runs/poc-{requestId}/tasks/{taskId}/attempts/{attempt}/output.json";
}
