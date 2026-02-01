using Microsoft.Graph.Models;
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
        /// <summary>
        /// Lists all application-scoped security groups.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A readonly list of security groups.</returns>
        Task<IReadOnlyList<SecurityGroup>> ListGroupsAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets a security group by its display name.
        /// </summary>
        /// <param name="groupName">The display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The security group if found; otherwise null.</returns>
        Task<SecurityGroup?> GetGroupByNameAsync(string groupName, CancellationToken ct = default);

        /// <summary>
        /// Ensures a security group exists, creating it if necessary.
        /// </summary>
        /// <param name="groupName">The display name of the group.</param>
        /// <param name="description">Optional description for the group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The ensured security group.</returns>
        Task<SecurityGroup> EnsureGroupAsync(string groupName, string? description = null, CancellationToken ct = default);

        /// <summary>
        /// Deletes a security group by its display name.
        /// </summary>
        /// <param name="groupName">The display name of the group to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        Task DeleteGroupByNameAsync(string groupName, CancellationToken ct = default);

        /// <summary>
        /// Searches for users by email or display name prefix.
        /// </summary>
        /// <param name="query">The search query.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A readonly list of matching users.</returns>
        Task<IReadOnlyList<AppUser>> SearchUsersAsync(string query, CancellationToken ct = default);

        /// <summary>
        /// Invites a user by email and optionally adds them to groups.
        /// Creates a guest user if the user does not exist.
        /// </summary>
        /// <param name="email">The user's email address.</param>
        /// <param name="displayName">Optional display name for the user.</param>
        /// <param name="groups">Optional list of group names to add the user to.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The invited or existing user.</returns>
        Task<AppUser> InviteUserAsync(string email, string? displayName, IEnumerable<string>? groups = null, CancellationToken ct = default);

        /// <summary>
        /// Adds a user to a security group.
        /// </summary>
        /// <param name="userEmail">The user's email address.</param>
        /// <param name="groupName">The display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        Task AddUserToGroupAsync(string userEmail, string groupName, CancellationToken ct = default);

        /// <summary>
        /// Removes a user from a security group.
        /// </summary>
        /// <param name="userEmail">The user's email address.</param>
        /// <param name="groupName">The display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        Task RemoveUserFromGroupAsync(string userEmail, string groupName, CancellationToken ct = default);

        /// <summary>
        /// Gets all members of a security group.
        /// </summary>
        /// <param name="groupName">The display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A readonly list of users in the group.</returns>
        Task<IReadOnlyList<AppUser>> GetGroupMembersAsync(string groupName, CancellationToken ct = default);

        /// <summary>
        /// Checks if a user is a member of a security group.
        /// </summary>
        /// <param name="userEmail">The user's email address.</param>
        /// <param name="groupName">The display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the user is a member; otherwise false.</returns>
        Task<bool> IsUserInGroupAsync(string userEmail, string groupName, CancellationToken ct = default);
    }

    /// <summary>
    /// Extensions to the <see cref="ISecurityGroupService"/>.
    /// </summary>
    public static class SecurityGroupServiceExtensions
    {
        /// <summary>
        /// Checks if a user has the required security permissions for a tenant/road.
        /// </summary>
        /// <param name="securityGroupService">The security group service.</param>
        /// <param name="securityLevel">The required security level flags.</param>
        /// <param name="user">The user's email address.</param>
        /// <param name="tenantId">The tenant ID.</param>
        /// <param name="roadId">Optional road ID for road-level permissions.</param>
        /// <returns>True if the user has the required permissions; otherwise false.</returns>
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
                .IsUserInGroupAsync(user, SecurityGroupNameBuilder.TenantRoadUser(tenantId, roadId));
            bool isTenantUser = await securityGroupService
                .IsUserInGroupAsync(user, SecurityGroupNameBuilder.TenantUser(tenantId));
            bool isTenantManager = await securityGroupService
                .IsUserInGroupAsync(user, SecurityGroupNameBuilder.TenantManager(tenantId));
            bool isGlobalAdmin = await securityGroupService
                .IsUserInGroupAsync(user, SecurityGroupNameBuilder.GlobalAdminsGroup);

            if (securityLevel.HasFlag(RequiredSecurityLevel.RoadUser) && isRoadUser)
            {
                return true;
            }

            if (securityLevel.HasFlag(RequiredSecurityLevel.TenantUser) && isTenantUser)
            {
                return true;
            }

            if (securityLevel.HasFlag(RequiredSecurityLevel.TenantManager) && isTenantManager)
            {
                return true;
            }

            if (securityLevel.HasFlag(RequiredSecurityLevel.Global) && isGlobalAdmin)
            {
                return true;
            }

            return false;

        }

        /// <summary>
        /// Gets the user's email from the claims principal.
        /// </summary>
        /// <param name="claimsPrincipal">The claims principal.</param>
        /// <returns>The user's email address or an empty string if not found.</returns>
        public static string GetEmail(this ClaimsPrincipal claimsPrincipal)
        {
            return claimsPrincipal.FindFirst("preferred_username")?.Value ??
                claimsPrincipal.Identity?.Name ??
                string.Empty;
        }
    }
}