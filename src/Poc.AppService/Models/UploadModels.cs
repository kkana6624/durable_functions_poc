using System.Text.Json.Serialization;

namespace Poc.AppService.Models;

public sealed record InitiateUploadRequest(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("expectedSizeBytes")] long ExpectedSizeBytes,
    [property: JsonPropertyName("contentType")] string? ContentType = null);

public sealed record InitiateUploadResponse(
    [property: JsonPropertyName("inputId")] string InputId,
    [property: JsonPropertyName("blobKey")] string BlobKey,
    [property: JsonPropertyName("container")] string Container);

public sealed record FinalizeUploadRequest(
    [property: JsonPropertyName("inputId")] string InputId,
    [property: JsonPropertyName("blobKey")] string BlobKey,
    [property: JsonPropertyName("expectedSizeBytes")] long ExpectedSizeBytes,
    [property: JsonPropertyName("expectedSha256")] string ExpectedSha256,
    [property: JsonPropertyName("versionId")] string? VersionId = null,
    [property: JsonPropertyName("etag")] string? Etag = null,
    [property: JsonPropertyName("container")] string? Container = null);

public sealed record FinalizeUploadResponse(
    [property: JsonPropertyName("readyManifestId")] string ReadyManifestId,
    [property: JsonPropertyName("blobKey")] string BlobKey,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256);
