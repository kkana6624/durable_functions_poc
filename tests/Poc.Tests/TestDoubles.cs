using System.Text.Json;
using Poc.Core;

namespace Poc.Tests;

// Deliberately test-only: these doubles do not demonstrate Azure connectivity or Durable replay.
internal sealed class FakeBatchGateway : IBatchGateway
{
    public Task<BatchJobTerminationStatus> TerminateJobAsync(WorkflowPlan plan, CancellationToken cancellationToken = default) => Task.FromResult(new BatchJobTerminationStatus("Completed", true));
    public Dictionary<string, string> Jobs { get; } = [];
    public Dictionary<string, string> Tasks { get; } = [];
    public int? MissingFileIndex { get; set; }

    public Task EnsureJobAsync(WorkflowPlan plan, CancellationToken cancellationToken = default)
    {
        Ensure(Jobs, plan.JobId, plan.Fingerprint);
        return Task.CompletedTask;
    }

    public Task EnsureTaskAsync(TaskSpec spec, CancellationToken cancellationToken = default)
    {
        Ensure(Tasks, spec.Plan.JobId + "/" + spec.TaskId, spec.Plan.Fingerprint);
        return Task.CompletedTask;
    }

    public Task<BatchTaskStatus> GetStatusAsync(TaskSpec spec, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BatchTaskStatus(spec.TaskId, "completed", true, !spec.InjectFailure,
            spec.InjectFailure ? 42 : 0, spec.InjectFailure ? "Injected failure" : null));

    public Task<string> ReadResultAsync(TaskSpec spec, CancellationToken cancellationToken = default) =>
        spec.Index == MissingFileIndex ? Task.FromException<string>(new FileNotFoundException("wd/result.txt")) : Task.FromResult("1\r\n");

    private static void Ensure(Dictionary<string, string> resources, string id, string fingerprint)
    {
        if (resources.TryGetValue(id, out var existing) && existing != fingerprint)
            throw new WorkflowConflictException("Existing resource has different input.");
        resources[id] = fingerprint;
    }
}

internal sealed class InMemoryResultRepository : IResultRepository, ITargetBlobVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public Dictionary<string, string> Documents { get; } = [];
    public Dictionary<string, byte[]> Blobs { get; } = [];
    public int Creates { get; private set; }
    public string GetUri(string path) => "https://test.invalid/poc-results/" + path;

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
            Documents.Add(path, json);
            Creates++;
        }
        return Task.FromResult(GetUri(path));
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

    public Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default) =>
        Documents.TryGetValue(path, out var json)
            ? Task.FromResult(JsonSerializer.Deserialize<T>(json, JsonOptions)!)
            : Task.FromException<T>(new FileNotFoundException(path));
}
