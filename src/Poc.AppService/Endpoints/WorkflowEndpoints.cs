using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Poc.AppService.Authentication;
using Poc.AppService.Clients;
using Poc.AppService.Models;
using Poc.Core;

namespace Poc.AppService.Endpoints;

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workflows");

        group.MapPost("/", async (
            StartWorkflowApiRequest request,
            IWorkflowClient workflowClient,
            IBlobStorageRepository storageRepository,
            IAppServiceCallerResolver callerResolver,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            WorkflowOwner? owner;
            try
            {
                owner = callerResolver.Resolve(httpContext);
            }
            catch (Exception ex) when (ex is ArgumentException or WorkflowForbiddenException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (owner is null)
            {
                return Results.Unauthorized();
            }

            WorkflowRequest workflowRequest;
            try
            {
                workflowRequest = new WorkflowRequest(
                    RequestId: request.RequestId,
                    Classification: request.Classification,
                    TaskCount: request.TaskCount,
                    FailTaskIndex: request.FailTaskIndex,
                    LightDelaySeconds: request.LightDelaySeconds,
                    HeavyDelaySeconds: request.HeavyDelaySeconds,
                    ReadyManifestId: request.ReadyManifestId,
                    SchemaVersion: request.SchemaVersion,
                    AlgorithmVersion: request.AlgorithmVersion,
                    Mode: request.Mode,
                    InputPayloadJson: request.InputPayloadJson);
                workflowRequest.Validate();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            // Early ReadyManifest verification and ownership check if specified
            if (!string.IsNullOrEmpty(workflowRequest.ReadyManifestId))
            {
                ReadyManifest? readyManifest;
                try
                {
                    readyManifest = await storageRepository.TryGetReadyManifestAsync(workflowRequest.ReadyManifestId, cancellationToken);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
                {
                    return Results.BadRequest(new { error = $"Ready manifest '{workflowRequest.ReadyManifestId}' is invalid." });
                }

                if (readyManifest is null)
                {
                    return Results.BadRequest(new { error = $"Ready manifest '{workflowRequest.ReadyManifestId}' not found." });
                }

                if (readyManifest.OwnerId is not null && !string.Equals(readyManifest.OwnerId, owner.OwnerId, StringComparison.Ordinal))
                {
                    return Results.Json(new { error = "Caller is not authorized to access this ready manifest." }, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            var result = await workflowClient.StartWorkflowAsync(workflowRequest, owner, cancellationToken);

            if (result.Success)
            {
                var pollingUrl = $"/api/workflows/{Uri.EscapeDataString(workflowRequest.RequestId)}";
                httpContext.Response.Headers.Append("Retry-After", "10");

                var responseBody = new StartWorkflowApiResponse(
                    RequestId: workflowRequest.RequestId,
                    InstanceId: result.InstanceId ?? WorkflowIdentity.InstanceId(workflowRequest.RequestId),
                    StatusQueryGetUri: pollingUrl,
                    Status: "Accepted");

                return Results.Accepted(pollingUrl, responseBody);
            }

            return result.StatusCode switch
            {
                StatusCodes.Status409Conflict => Results.Conflict(new { error = result.Error ?? "requestId already has different configuration.", requestId = workflowRequest.RequestId }),
                StatusCodes.Status403Forbidden => Results.Json(new { error = result.Error ?? "Caller is not authorized to access this workflow." }, statusCode: StatusCodes.Status403Forbidden),
                StatusCodes.Status400BadRequest => Results.BadRequest(new { error = result.Error ?? "Workflow request is invalid." }),
                StatusCodes.Status503ServiceUnavailable => Results.Json(new { error = result.Error ?? "Admission could not be confirmed. Retry with the same requestId." }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(new { error = result.Error ?? "Workflow admission failed." }, statusCode: result.StatusCode)
            };
        })
        .WithName("StartWorkflow")
        .Produces<StartWorkflowApiResponse>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/{requestId}", async (
            string requestId,
            IWorkflowClient workflowClient,
            IAppServiceCallerResolver callerResolver,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            WorkflowOwner? owner;
            try
            {
                owner = callerResolver.Resolve(httpContext);
            }
            catch (Exception ex) when (ex is ArgumentException or WorkflowForbiddenException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (owner is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                new WorkflowRequest(requestId).Validate();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var result = await workflowClient.GetStatusAsync(requestId, owner, cancellationToken);

            if (result.Success && result.Status is not null)
            {
                var pollUrl = $"/api/workflows/{Uri.EscapeDataString(requestId)}";
                // Proxy strictly safe sanitized JSON - never reveal Durable management URLs or internal keys
                var sanitized = new AppServiceWorkflowStatusResponse(
                    RequestId: result.Status.RequestId,
                    Status: result.Status.Status,
                    ResultAvailable: result.Status.ResultAvailable,
                    InstanceId: result.Status.InstanceId,
                    JobId: result.Status.JobId,
                    TerminationConfirmed: result.Status.TerminationConfirmed,
                    Error: result.Status.Error,
                    PollUrl: pollUrl);

                return Results.Ok(sanitized);
            }

            return result.StatusCode switch
            {
                StatusCodes.Status404NotFound => Results.NotFound(new { error = result.Error ?? "Workflow not found.", requestId }),
                StatusCodes.Status403Forbidden => Results.Json(new { error = result.Error ?? "Caller is not authorized to access this workflow." }, statusCode: StatusCodes.Status403Forbidden),
                StatusCodes.Status503ServiceUnavailable => Results.Json(new { error = result.Error ?? "Workflow status could not be verified. Retry later." }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(new { error = result.Error ?? "Status query failed." }, statusCode: result.StatusCode)
            };
        })
        .WithName("GetWorkflowStatus")
        .Produces<AppServiceWorkflowStatusResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{requestId}/cancel", async (
            string requestId,
            CancelWorkflowApiRequest? cancelRequest,
            IWorkflowClient workflowClient,
            IAppServiceCallerResolver callerResolver,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            WorkflowOwner? owner;
            try
            {
                owner = callerResolver.Resolve(httpContext);
            }
            catch (Exception ex) when (ex is ArgumentException or WorkflowForbiddenException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (owner is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                new WorkflowRequest(requestId).Validate();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var result = await workflowClient.CancelWorkflowAsync(requestId, owner, cancelRequest?.Reason, cancellationToken);

            if (result.Success)
            {
                if (result.StatusCode == StatusCodes.Status202Accepted)
                {
                    return Results.Accepted(null, new { requestId, status = "Cancelling" });
                }

                return Results.Ok(new CancelWorkflowApiResponse(
                    RequestId: requestId,
                    Status: result.Status?.Status ?? "Cancelled",
                    TerminationConfirmed: result.Status?.TerminationConfirmed,
                    Error: result.Status?.Error));
            }

            return result.StatusCode switch
            {
                StatusCodes.Status404NotFound => Results.NotFound(new { error = result.Error ?? "Workflow not found.", requestId }),
                StatusCodes.Status409Conflict => Results.Conflict(new { error = result.Error ?? "Workflow cannot be cancelled in its current state.", requestId }),
                StatusCodes.Status403Forbidden => Results.Json(new { error = result.Error ?? "Caller is not authorized to cancel this workflow." }, statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Json(new { error = result.Error ?? "Cancellation failed." }, statusCode: result.StatusCode)
            };
        })
        .WithName("CancelWorkflow")
        .Produces<CancelWorkflowApiResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict);

        return endpoints;
    }
}
