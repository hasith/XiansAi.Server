using Shared.Repositories;
using Shared.Utils.Services;
using Shared.Utils;

namespace Shared.Services;

/// <summary>
/// Validates activation state for webhook and message routing.
/// Uses a shared generic cache for consistent performance across webhooks and Admin API.
/// </summary>
public interface IActivationValidationService
{
    /// <summary>
    /// Validates that the specified activation exists and is active.
    /// Use when routing webhooks, messages, or API requests to a specific activation instance.
    /// Results are cached to reduce database load on repeated calls.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="activationName">The activation name (workflow ID postfix).</param>
    /// <param name="workflowType">Optional. When provided, validates that the agent has a flow definition for this workflow type (e.g. "Supervisor Workflow", "Integrator Workflow").</param>
    /// <returns>Success if activation exists and is active; NotFound if not found; Conflict if deactivated; BadRequest if workflow type not registered for agent.</returns>
    Task<ServiceResult> ValidateActivationAsync(string tenantId, string agentName, string activationName, string? workflowType = null);

    /// <summary>
    /// Invalidates the cached validation result for an activation.
    /// Call when an activation is deactivated or deleted to ensure subsequent requests fail immediately.
    /// </summary>
    void InvalidateActivationCache(string tenantId, string agentName, string activationName);

    /// <summary>
    /// Returns the ordered workflow input values configured for an active activation and workflow type.
    /// </summary>
    Task<ServiceResult<object[]>> GetWorkflowInputsAsync(
        string tenantId,
        string agentName,
        string activationName,
        string workflowType,
        string workflowId);
}

public class ActivationValidationService : IActivationValidationService
{
    private const string CacheKeyPrefix = "activation:validation:";
    private const string WorkflowTypeCacheKeyPrefix = "activation:workflow-type:";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
    // Flow definitions are rarely changed at runtime — cache for 5 minutes to reduce DB load
    // on every adminapi /send, /listen, /history, /topics call.
    private static readonly TimeSpan WorkflowTypeCacheDuration = TimeSpan.FromMinutes(5);

    private readonly IActivationRepository _activationRepository;
    private readonly IFlowDefinitionRepository _flowDefinitionRepository;
    private readonly IAgentRepository _agentRepository;
    private readonly IAsyncResultCache _cache;
    private readonly ILogger<ActivationValidationService> _logger;

    public ActivationValidationService(
        IActivationRepository activationRepository,
        IFlowDefinitionRepository flowDefinitionRepository,
        IAgentRepository agentRepository,
        IAsyncResultCache cache,
        ILogger<ActivationValidationService> logger)
    {
        _activationRepository = activationRepository ?? throw new ArgumentNullException(nameof(activationRepository));
        _flowDefinitionRepository = flowDefinitionRepository ?? throw new ArgumentNullException(nameof(flowDefinitionRepository));
        _agentRepository = agentRepository ?? throw new ArgumentNullException(nameof(agentRepository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ServiceResult> ValidateActivationAsync(string tenantId, string agentName, string activationName, string? workflowType = null)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            return ServiceResult.Failure("TenantId is required", StatusCode.BadRequest);
        if (string.IsNullOrWhiteSpace(agentName))
            return ServiceResult.Failure("AgentName is required", StatusCode.BadRequest);
        if (string.IsNullOrWhiteSpace(activationName))
            return ServiceResult.Failure("ActivationName is required", StatusCode.BadRequest);

        var cacheKey = BuildCacheKey(tenantId, agentName, activationName);
        var result = await _cache.GetOrAddAsync(
            cacheKey,
            _ => ValidateFromRepositoryAsync(tenantId, agentName, activationName),
            CacheDuration,
            size: 1);
        if (!result.IsSuccess)
            return result;

        // Optionally validate that the agent has the requested workflow type registered.
        // Result is cached separately so repeated calls to /send, /listen, /history, /topics
        // with the same (tenant, agent, workflowType) do not re-query the flow_definition
        // collection on every request.
        if (!string.IsNullOrWhiteSpace(workflowType))
        {
            var workflowTypeCacheKey = BuildWorkflowTypeCacheKey(tenantId, agentName, workflowType.Trim());
            var workflowCheck = await _cache.GetOrAddAsync(
                workflowTypeCacheKey,
                _ => ValidateWorkflowTypeForAgentAsync(tenantId, agentName, workflowType.Trim()),
                WorkflowTypeCacheDuration,
                size: 1);
            if (!workflowCheck.IsSuccess)
                return workflowCheck;
        }

        return ServiceResult.Success();
    }

    public void InvalidateActivationCache(string tenantId, string agentName, string activationName)
    {
        _cache.Remove(BuildCacheKey(tenantId, agentName, activationName));
    }

    public async Task<ServiceResult<object[]>> GetWorkflowInputsAsync(
        string tenantId,
        string agentName,
        string activationName,
        string workflowType,
        string workflowId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("TenantId could not be resolved from certificate context");
            return ServiceResult<object[]>.Failure("TenantId could not be resolved", StatusCode.BadRequest);
        }
        if (string.IsNullOrWhiteSpace(agentName))
            return ServiceResult<object[]>.Failure("AgentName is required", StatusCode.BadRequest);
        if (string.IsNullOrWhiteSpace(activationName))
            return ServiceResult<object[]>.Failure("ActivationName is required", StatusCode.BadRequest);
        if (string.IsNullOrWhiteSpace(workflowType))
            return ServiceResult<object[]>.Failure("WorkflowType is required", StatusCode.BadRequest);
        if (string.IsNullOrWhiteSpace(workflowId))
            return ServiceResult<object[]>.Failure("WorkflowId is required", StatusCode.BadRequest);

        _logger.LogInformation(
            "Retrieving workflow inputs: activationName={ActivationName}, agentName={AgentName}, workflowType={WorkflowType}, workflowId={WorkflowId}, tenantId={TenantId}",
            LogSanitizer.Sanitize(activationName),
            LogSanitizer.Sanitize(agentName),
            LogSanitizer.Sanitize(workflowType),
            LogSanitizer.Sanitize(workflowId),
            LogSanitizer.Sanitize(tenantId));

        try
        {
            var activation = await _activationRepository.GetByNameAndAgentAsync(tenantId, agentName, activationName);
            if (activation == null)
            {
                _logger.LogWarning(
                    "Activation not found: activationName={ActivationName}, agentName={AgentName}, tenantId={TenantId}",
                    LogSanitizer.Sanitize(activationName),
                    LogSanitizer.Sanitize(agentName),
                    LogSanitizer.Sanitize(tenantId));
                return ServiceResult<object[]>.NotFound(
                    $"Activation '{activationName}' not found for agent '{agentName}'");
            }

            if (!activation.IsActive)
            {
                _logger.LogWarning(
                    "Activation '{ActivationName}' for agent '{AgentName}' in tenant '{TenantId}' is not active",
                    LogSanitizer.Sanitize(activationName),
                    LogSanitizer.Sanitize(agentName),
                    LogSanitizer.Sanitize(tenantId));
                return ServiceResult<object[]>.Failure(
                    $"Activation '{activationName}' is not active",
                    StatusCode.BadRequest);
            }

            if (!activation.WorkflowIds.Contains(workflowId))
            {
                _logger.LogWarning(
                    "WorkflowId '{WorkflowId}' is not registered in activation '{ActivationName}'",
                    LogSanitizer.Sanitize(workflowId),
                    LogSanitizer.Sanitize(activationName));
                return ServiceResult<object[]>.Failure(
                    $"WorkflowId '{workflowId}' is not registered in activation '{activationName}'",
                    StatusCode.BadRequest);
            }

            var workflowConfig = activation.WorkflowConfiguration?.Workflows?
                .FirstOrDefault(w => w.WorkflowType == workflowType);
            if (workflowConfig == null)
            {
                _logger.LogWarning(
                    "WorkflowType '{WorkflowType}' not found in activation '{ActivationName}'",
                    LogSanitizer.Sanitize(workflowType),
                    LogSanitizer.Sanitize(activationName));
                return ServiceResult<object[]>.Success(Array.Empty<object>());
            }

            var inputValues = workflowConfig.Inputs?
                .Select(input => (object)input.Value)
                .ToArray() ?? Array.Empty<object>();

            _logger.LogInformation(
                "Returning {Count} workflow input(s) for workflowType={WorkflowType}, workflowId={WorkflowId}, activationName={ActivationName}",
                inputValues.Length,
                LogSanitizer.Sanitize(workflowType),
                LogSanitizer.Sanitize(workflowId),
                LogSanitizer.Sanitize(activationName));

            return ServiceResult<object[]>.Success(inputValues);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error retrieving workflow inputs for activation '{ActivationName}', agent '{AgentName}', tenant {TenantId}",
                LogSanitizer.Sanitize(activationName),
                LogSanitizer.Sanitize(agentName),
                LogSanitizer.Sanitize(tenantId));
            return ServiceResult<object[]>.InternalServerError(
                "An error occurred while retrieving workflow inputs");
        }
    }

    private static string BuildCacheKey(string tenantId, string agentName, string activationName)
        => $"{CacheKeyPrefix}{tenantId}\x01{agentName}\x01{activationName}";

    private static string BuildWorkflowTypeCacheKey(string tenantId, string agentName, string workflowType)
        => $"{WorkflowTypeCacheKeyPrefix}{tenantId}\x01{agentName}\x01{workflowType}";

    private async Task<ServiceResult> ValidateWorkflowTypeForAgentAsync(string tenantId, string agentName, string workflowType)
    {
        try
        {

            var flowDefinitions = await _flowDefinitionRepository.GetByNameAsync(agentName, tenantId);
            if (flowDefinitions == null || flowDefinitions.Count == 0)
            {
                _logger.LogWarning("No workflow definitions found for agent '{AgentName}' in tenant {TenantId}", LogSanitizer.Sanitize(agentName), LogSanitizer.Sanitize(tenantId));
                return ServiceResult.Failure(
                    $" No agent process registered for agent '{agentName}'. Unable to use this agent for this purpose.",
                    StatusCode.BadRequest);
            }

            workflowType = $"{agentName}:{workflowType}";

            var hasWorkflow = flowDefinitions.Any(fd =>
                string.Equals(fd.WorkflowType, workflowType, StringComparison.Ordinal));
            if (!hasWorkflow)
            {
                var registeredTypes = flowDefinitions
                    .Select(fd => fd.WorkflowType.Contains(':') ? fd.WorkflowType.Split(':').LastOrDefault()?.Trim() : fd.WorkflowType)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList();
                var registeredList = registeredTypes.Count > 0 ? string.Join(", ", registeredTypes) : "none";
                _logger.LogWarning(
                    "Workflow type '{WorkflowType}' is not registered for agent '{AgentName}'. Registered types: {Registered}",
                    LogSanitizer.Sanitize(workflowType), LogSanitizer.Sanitize(agentName), LogSanitizer.Sanitize(registeredList));
                return ServiceResult.Failure(
                    $"Workflow type '{workflowType}' is not registered for agent '{agentName}'. Registered workflow types: {registeredList}.",
                    StatusCode.BadRequest);
            }

            return ServiceResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error validating workflow type '{WorkflowType}' for agent '{AgentName}' in tenant {TenantId}",
                LogSanitizer.Sanitize(workflowType), LogSanitizer.Sanitize(agentName), LogSanitizer.Sanitize(tenantId));
            return ServiceResult.InternalServerError(
                "An error occurred while validating the workflow type");
        }
    }

    private async Task<ServiceResult> ValidateFromRepositoryAsync(string tenantId, string agentName, string activationName)
    {
        try
        {
            var activation = await _activationRepository.GetByNameAndAgentAsync(tenantId, agentName, activationName);
            if (activation == null)
            {
                _logger.LogWarning(
                    "Activation '{ActivationName}' not found for agent '{AgentName}' in tenant {TenantId}",
                    LogSanitizer.Sanitize(activationName), LogSanitizer.Sanitize(agentName), LogSanitizer.Sanitize(tenantId));
                return ServiceResult.Failure(
                    $"Activation '{activationName}' not found for agent '{agentName}'",
                    StatusCode.NotFound);
            }

            if (!activation.IsActive)
            {
                _logger.LogWarning(
                    "Activation '{ActivationName}' for agent '{AgentName}' is deactivated",
                    LogSanitizer.Sanitize(activationName), LogSanitizer.Sanitize(agentName));
                return ServiceResult.Failure(
                    $"Activation '{activationName}' is deactivated",
                    StatusCode.Conflict);
            }

            return ServiceResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error validating activation '{ActivationName}' for agent '{AgentName}' in tenant {TenantId}",
                LogSanitizer.Sanitize(activationName), LogSanitizer.Sanitize(agentName), LogSanitizer.Sanitize(tenantId));
            return ServiceResult.InternalServerError(
                "An error occurred while validating the activation");
        }
    }
}
