using Microsoft.AspNetCore.Mvc;
using Features.AgentApi.Auth;
using Shared.Auth;
using Shared.Services;
using Shared.Utils.Services;

namespace Features.AgentApi.Endpoints;

/// <summary>
/// Provides extension methods for registering activation-related API endpoints for agents.
/// </summary>
public static class ActivationEndpoints
{
    /// <summary>
    /// Maps all activation-related endpoints to the application's request pipeline.
    /// </summary>
    public static void MapActivationEndpoints(this WebApplication app)
    {
        var activationGroup = app.MapGroup("/api/agent/activation")
            .WithTags("AgentAPI - Activation")
            .RequiresCertificate();

        activationGroup.MapGet("/workflow-inputs", async (
            [FromQuery] string activationName,
            [FromQuery] string agentName,
            [FromQuery] string workflowType,
            [FromQuery] string workflowId,
            [FromServices] IActivationValidationService activationValidationService,
            [FromServices] ITenantContext tenantContext) =>
        {
            var result = await activationValidationService.GetWorkflowInputsAsync(
                tenantContext.TenantId,
                agentName,
                activationName,
                workflowType,
                workflowId);
            return result.ToHttpResult();
        })
        .WithName("Get Workflow Input Parameters")
        .Produces<object[]>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .WithSummary("Get workflow input parameters for an activation")
        .WithDescription("Returns the ordered list of input values configured for a specific workflow type ");

        activationGroup.MapGet("/exists", async (
            [FromQuery] string activationName,
            [FromQuery] string agentName,
            [FromServices] IActivationValidationService activationValidationService,
            [FromServices] ITenantContext tenantContext) =>
        {
            var result = await activationValidationService.ValidateActivationAsync(
                tenantContext.TenantId,
                agentName,
                activationName);
            return result.ToHttpResult();
        })
        .WithName("Check Activation Exists")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .WithSummary("Check whether an activation exists and is active")
        .WithDescription("Returns 200 when the activation exists and is active for the agent in the current tenant; 404 when not found; 409 when deactivated.");
    }
}
