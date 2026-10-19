using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Poc.AppService.Authentication;
using Poc.AppService.Models;
using Poc.AppService.Services;
using Poc.Core;

namespace Poc.AppService.Endpoints;

public static class UploadEndpoints
{
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/uploads");

        group.MapPost("/initiate", (
            InitiateUploadRequest request,
            IUploadService uploadService,
            IAppServiceCallerResolver callerResolver,
            HttpContext httpContext) =>
        {
            WorkflowOwner? owner;
            try
            {
                owner = callerResolver.Resolve(httpContext);
            }
            catch (WorkflowForbiddenException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (owner is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var response = uploadService.InitiateUpload(request, owner);
                return Results.Ok(response);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("InitiateUpload")
        .Produces<InitiateUploadResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/finalize", async (
            FinalizeUploadRequest request,
            IUploadService uploadService,
            IAppServiceCallerResolver callerResolver,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            WorkflowOwner? owner;
            try
            {
                owner = callerResolver.Resolve(httpContext);
            }
            catch (WorkflowForbiddenException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (owner is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var response = await uploadService.FinalizeUploadAsync(request, owner, cancellationToken);
                return Results.Ok(response);
            }
            catch (WorkflowForbiddenException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (WorkflowConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FileNotFoundException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Azure.RequestFailedException ex) when (ex.Status is 404 or 412)
            {
                return Results.BadRequest(new { error = $"Target blob not found or version condition failed (HTTP {ex.Status})." });
            }
        })
        .WithName("FinalizeUpload")
        .Produces<FinalizeUploadResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status409Conflict);

        return endpoints;
    }
}
