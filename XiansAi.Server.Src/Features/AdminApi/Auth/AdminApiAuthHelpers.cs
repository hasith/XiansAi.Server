using Microsoft.AspNetCore.Http;
using Shared.Auth;

namespace Features.AdminApi.Auth;

/// <summary>
/// Shared request parsing and tenant-context helpers for Admin API authentication
/// and authorization handlers.
/// </summary>
internal static class AdminApiAuthHelpers
{
    internal const string BearerPrefix = "Bearer ";
    internal const string TenantIdHeader = "X-Tenant-Id";

    /// <summary>
    /// Returns true when <see cref="AdminEndpointAuthenticationHandler"/> already populated
    /// <paramref name="tenantContext"/> with a valid admin role.
    /// </summary>
    internal static bool IsContextPopulatedByAuthentication(ITenantContext tenantContext)
    {
        if (string.IsNullOrEmpty(tenantContext.LoggedInUser) ||
            string.IsNullOrEmpty(tenantContext.TenantId) ||
            tenantContext.UserRoles is null)
        {
            return false;
        }

        return tenantContext.UserRoles.Contains(SystemRoles.SysAdmin) ||
               tenantContext.UserRoles.Contains(SystemRoles.TenantAdmin);
    }

    /// <summary>
    /// Reads the bearer token from the Authorization header only.
    /// Query parameters are intentionally not supported (they leak into logs and browser history).
    /// </summary>
    internal static string? ExtractBearerToken(HttpRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        var authHeader = request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(authHeader) ||
            !authHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return authHeader[BearerPrefix.Length..].Trim();
    }

    /// <summary>
    /// Tenant ID sources in priority order:
    /// 1. Query parameter (tenantId=)
    /// 2. Route parameter (e.g. /tenants/{tenantId})
    /// 3. X-Tenant-Id header
    /// </summary>
    internal static string ExtractTenantId(HttpRequest? request)
    {
        if (request is null)
        {
            return string.Empty;
        }

        var tenantId = request.Query["tenantId"].ToString();
        if (!string.IsNullOrEmpty(tenantId))
        {
            return tenantId;
        }

        if (request.RouteValues.TryGetValue("tenantId", out var routeTenantId) && routeTenantId is not null)
        {
            return routeTenantId.ToString() ?? string.Empty;
        }

        return request.Headers[TenantIdHeader].FirstOrDefault() ?? string.Empty;
    }

    internal static void ApplyResolutionToTenantContext(
        ITenantContext tenantContext,
        string userId,
        string accessToken,
        AdminRoleTenantResolutionResult resolutionResult)
    {
        var finalTenantId = resolutionResult.FinalTenantId!;
        var userRoles = resolutionResult.UserRoles!;

        tenantContext.LoggedInUser = userId;
        tenantContext.UserType = UserType.UserApiKey;
        tenantContext.TenantId = finalTenantId;
        tenantContext.UserRoles = userRoles;
        tenantContext.AuthorizedTenantIds = [finalTenantId];
        tenantContext.Authorization = accessToken;
    }
}
