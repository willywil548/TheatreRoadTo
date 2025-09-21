using System.Collections.Concurrent;

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
}