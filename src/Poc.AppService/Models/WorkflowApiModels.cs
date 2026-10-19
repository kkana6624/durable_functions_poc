using System.Text.Json.Serialization;

namespace Poc.AppService.Models;

public sealed record StartWorkflowApiRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("readyManifestId")] string? ReadyManifestId = null,
    [property: JsonPropertyName("classification")] string Classification = "auto",
    [property: JsonPropertyName("taskCount")] int TaskCount = 3,
    [property: JsonPropertyName("failTaskIndex")] int? FailTaskIndex = null,
    [property: JsonPropertyName("lightDelaySeconds")] int LightDelaySeconds = 5,
    [property: JsonPropertyName("heavyDelaySeconds")] int HeavyDelaySeconds = 15,
    [property: JsonPropertyName("schemaVersion")] int? SchemaVersion = null,
    [property: JsonPropertyName("algorithmVersion")] string? AlgorithmVersion = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("inputPayloadJson")] string? InputPayloadJson = null);

public sealed record StartWorkflowApiResponse(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("statusQueryGetUri")] string StatusQueryGetUri,
    [property: JsonPropertyName("status")] string Status = "Accepted");

public sealed record AppServiceWorkflowStatusResponse(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("resultAvailable")] bool ResultAvailable,
    [property: JsonPropertyName("instanceId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstanceId = null,
    [property: JsonPropertyName("jobId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? JobId = null,
    [property: JsonPropertyName("terminationConfirmed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TerminationConfirmed = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    [property: JsonPropertyName("pollUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PollUrl = null);

public sealed record CancelWorkflowApiRequest(
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record CancelWorkflowApiResponse(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("terminationConfirmed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TerminationConfirmed = null,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);
