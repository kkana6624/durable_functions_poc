using Poc.AppService.Models;
using Poc.Core;

namespace Poc.AppService.Services;

public interface IUploadService
{
    InitiateUploadResponse InitiateUpload(InitiateUploadRequest request, WorkflowOwner owner);
    Task<FinalizeUploadResponse> FinalizeUploadAsync(FinalizeUploadRequest request, WorkflowOwner owner, CancellationToken cancellationToken = default);
}
