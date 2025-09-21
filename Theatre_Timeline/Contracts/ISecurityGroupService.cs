using System.Security.Claims;
using Theatre_TimeLine.Services;

namespace Theatre_TimeLine.Contracts
{
    /// <summary>
    /// Abstraction over the identity provider to manage security groups and memberships.
    /// Email is treated as the unique user key externally; internally the provider resolves objectIds.
    /// </summary>
    public interface ISecurityGroupService
    {
        Task<IReadOnlyList<SecurityGroup>> ListGroupsAsync(CancellationToken ct = default);
        Task<SecurityGroup?> GetGroupByNameAsync(string groupName, CancellationToken ct = default);
        Task<SecurityGroup> EnsureGroupAsync(string groupName, string? description = null, CancellationToken ct = default);
        Task DeleteGroupByNameAsync(string groupName, CancellationToken ct = default);

        Task<IReadOnlyList<AppUser>> SearchUsersAsync(string query, CancellationToken ct = default);

        // Invite (creates guest if needed) and optionally add to groups
        Task<AppUser> InviteUserAsync(string email, string? displayName, IEnumerable<string>? groups = null, CancellationToken ct = default);

        Task AddUserToGroupAsync(string userEmail, string groupName, CancellationToken ct = default);
        Task RemoveUserFromGroupAsync(string userEmail, string groupName, CancellationToken ct = default);

        Task<IReadOnlyList<AppUser>> GetGroupMembersAsync(string groupName, CancellationToken ct = default);
        Task<bool> IsUserInGroupAsync(string userEmail, string groupName, CancellationToken ct = default);
    }

    /// <summary>
    /// Extensions to the <see cref="GraphSecurityGroupService"/>.
    /// </summary>
    public static class SecurityGroupServiceExtensions
    {
        internal static async Task<bool> HasRequiredPerms(
            this ISecurityGroupService securityGroupService,
            RequiredSecurityLevel securityLevel,
            string? user,
            Guid tenantId,
            Guid roadId = default)
        {
            if (string.IsNullOrEmpty(user))
            {
                return false;
            }

            if (string.Equals(tenantId.ToString(), TenantManagerService.DemoGuid))
            {
                return true;
            }

            bool isRoadUser = await securityGroupService
                .IsUserInGroupAsync(user, SecurityGroupNameBuilder.TenantRoadUser(tenantId, roadId)).ConfigureAwait(false);
            bool isTenantUser = await securityGroupService
                .IsUserInGroupAsync(user, SecurityGroupNameBuilder.TenantUser(tenantId)).ConfigureAwait(false);
            bool isTenantManager = await securityGroupService
                .IsUserInGroupAsync(user, SecurityGroupNameBuilder.TenantManager(tenantId)).ConfigureAwait(false);
            bool isGlobalAdmin = await securityGroupService
                .IsUserInGroupAsync(user, SecurityGroupNameBuilder.GlobalAdminsGroup).ConfigureAwait(false);

            return securityLevel switch
            {
                RequiredSecurityLevel.RoadUser => isRoadUser || isTenantManager || isGlobalAdmin,
                RequiredSecurityLevel.TenantUser => isRoadUser || isTenantUser || isTenantManager || isGlobalAdmin,
                RequiredSecurityLevel.TenantManager => isTenantManager || isGlobalAdmin,
                RequiredSecurityLevel.Global => isGlobalAdmin,
                _ => false
            };
        }

        public static string GetEmail(this ClaimsPrincipal claimsPrincipal)
        {
            return claimsPrincipal.FindFirst("preferred_username")?.Value ??
                claimsPrincipal.Identity?.Name ??
                string.Empty;
        }
    }
}