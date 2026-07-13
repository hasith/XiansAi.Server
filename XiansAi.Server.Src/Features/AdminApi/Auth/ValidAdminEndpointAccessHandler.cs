using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Shared.Auth;
using Shared.Exceptions;
using Shared.Services;
using Shared.Utils;

namespace Features.AdminApi.Auth;

/// <summary>
/// Authorization handler for Admin API endpoints. Confirms the caller holds an admin role
/// and populates <see cref="ITenantContext"/> when authentication did not already do so.
/// </summary>
public sealed class ValidAdminEndpointAccessHandler : AuthorizationHandler<ValidAdminEndpointAccessRequirement>
{
    private readonly ILogger<ValidAdminEndpointAccessHandler> _logger;
    private readonly ITenantContext _tenantContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IApiKeyService _apiKeyService;
    private readonly IAdminRoleTenantResolver _adminRoleTenantResolver;

    public ValidAdminEndpointAccessHandler(
        ITenantContext tenantContext,
        IHttpContextAccessor httpContextAccessor,
        IApiKeyService apiKeyService,
        IAdminRoleTenantResolver adminRoleTenantResolver,
        ILogger<ValidAdminEndpointAccessHandler> logger)
    {
        _tenantContext = tenantContext;
        _httpContextAccessor = httpContextAccessor;
        _apiKeyService = apiKeyService;
        _adminRoleTenantResolver = adminRoleTenantResolver;
        _logger = logger;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ValidAdminEndpointAccessRequirement requirement)
    {
        if (_tenantContext is null)
        {
            _logger.LogError("Failed to resolve ITenantContext from request scope");
            context.Fail();
            return;
        }

        var loggedInUser = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(loggedInUser))
        {
            _logger.LogWarning("No logged-in user found for AdminApi authorization");
            context.Fail();
            return;
        }

        if (AdminApiAuthHelpers.IsContextPopulatedByAuthentication(_tenantContext))
        {
            _logger.LogDebug(
                "AdminApi authorization: TenantContext already populated by authentication - skipping redundant resolution");
            context.Succeed(requirement);
            return;
        }

        try
        {
            if (await TryResolveAndPopulateContextAsync(loggedInUser))
            {
                context.Succeed(requirement);
            }
            else
            {
                context.Fail();
            }
        }
        catch (TenantNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing authorization for AdminApi endpoint connection");
            context.Fail();
        }
    }

    private async Task<bool> TryResolveAndPopulateContextAsync(string loggedInUser)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var accessToken = ResolveAccessToken(httpContext);
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning("No access token found for AdminApi authorization validation");
            return false;
        }

        var apiKey = await _apiKeyService.GetApiKeyByRawKeyAsync(accessToken);
        if (apiKey is null)
        {
            _logger.LogWarning("Invalid API key for AdminApi authorization validation");
            return false;
        }

        var tenantIdFromRequest = AdminApiAuthHelpers.ExtractTenantId(httpContext?.Request);
        var resolutionResult = await _adminRoleTenantResolver.ResolveAsync(
            loggedInUser, apiKey, tenantIdFromRequest);

        if (!resolutionResult.Success)
        {
            _logger.LogWarning(
                "Admin role resolution failed: {Error}",
                LogSanitizer.Sanitize(resolutionResult.ErrorMessage));
            return false;
        }

        var finalTenantId = resolutionResult.FinalTenantId!;
        var userRoles = resolutionResult.UserRoles!;

        _logger.LogDebug(
            "Setting tenant context with user ID: {UserId}, user type: {UserType}, and roles: {Roles}",
            loggedInUser,
            UserType.UserApiKey,
            string.Join(", ", userRoles));

        AdminApiAuthHelpers.ApplyResolutionToTenantContext(
            _tenantContext, loggedInUser, accessToken, resolutionResult);

        _logger.LogInformation(
            "Successfully authorized AdminApi endpoint connection: User={UserId}, Tenant={TenantId}, Roles={Roles}",
            LogSanitizer.Sanitize(loggedInUser),
            LogSanitizer.Sanitize(finalTenantId),
            LogSanitizer.Sanitize(string.Join(", ", userRoles)));

        return true;
    }

    private string? ResolveAccessToken(HttpContext? httpContext)
    {
        // Always extract from request headers to prevent TOCTOU attacks
        return AdminApiAuthHelpers.ExtractBearerToken(httpContext?.Request);
    }
        {
            return _tenantContext.Authorization;
        }

        return AdminApiAuthHelpers.ExtractBearerToken(httpContext?.Request);
    }
}
