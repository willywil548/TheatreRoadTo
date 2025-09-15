using System.Collections.Concurrent;
using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Services
{
    internal sealed class StubSecurityGroupService : ISecurityGroupService
    {
        private readonly ConcurrentDictionary<String, SecurityGroup> _groups = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<String, AppUser> _users = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<String, ConcurrentDictionary<String, byte>> _groupMembers = new(StringComparer.OrdinalIgnoreCase);

        public StubSecurityGroupService()
        {
            _groups.TryAdd(SecurityGroupNameBuilder.GlobalAdminsGroup, new SecurityGroup
            {
                Name = SecurityGroupNameBuilder.GlobalAdminsGroup,
                Description = "Global administrators with access to all sub-tenants and roads.",
                MemberCount = 0
            });
            _groupMembers.TryAdd(SecurityGroupNameBuilder.GlobalAdminsGroup, new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        }

        public Task<IReadOnlyList<SecurityGroup>> ListGroupsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SecurityGroup>>(
                _groups.Values
                       .Where(g => g.Name.StartsWith(SecurityGroupNameBuilder.AppGroupPrefix, StringComparison.OrdinalIgnoreCase))
                       .Select(UpdateCount)
                       .ToList());

        public Task<SecurityGroup?> GetGroupByNameAsync(string groupName, CancellationToken ct = default)
        {
            _groups.TryGetValue(groupName, out var g);
            return Task.FromResult<SecurityGroup?>(g is null ? null : UpdateCount(g));
        }

        public Task<SecurityGroup> EnsureGroupAsync(string groupName, string? description = null, CancellationToken ct = default)
        {
            var g = _groups.GetOrAdd(groupName, n => new SecurityGroup { Name = n, Description = description ?? "" });
            _groupMembers.TryAdd(groupName, new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            return Task.FromResult(UpdateCount(g));
        }

        public Task DeleteGroupByNameAsync(string groupName, CancellationToken ct = default)
        {
            _groups.TryRemove(groupName, out _);
            _groupMembers.TryRemove(groupName, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AppUser>> SearchUsersAsync(string query, CancellationToken ct = default)
        {
            var res = _users.Values
                .Where(u => u.Email.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || u.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return Task.FromResult<IReadOnlyList<AppUser>>(res);
        }

        public Task<AppUser> InviteUserAsync(string email, string? displayName, IEnumerable<string>? groups = null, CancellationToken ct = default)
        {
            var user = _users.GetOrAdd(email, e => new AppUser { Id = e, Email = e, DisplayName = displayName ?? e });
            if (groups != null)
            {
                foreach (var group in groups)
                {
                    _ = EnsureGroupAsync(group, ct: ct).Result;
                    _groupMembers[group].TryAdd(user.Email, 1);
                }
            }
            return Task.FromResult(user);
        }

        public async Task AddUserToGroupAsync(string userEmail, string groupName, CancellationToken ct = default)
        {
            await EnsureGroupAsync(groupName, ct: ct).ConfigureAwait(false);
            _users.TryAdd(userEmail, new AppUser { Id = userEmail, Email = userEmail, DisplayName = userEmail });
            _groupMembers[groupName].TryAdd(userEmail, 1);
        }

        public Task RemoveUserFromGroupAsync(string userEmail, string groupName, CancellationToken ct = default)
        {
            if (_groupMembers.TryGetValue(groupName, out var members))
            {
                members.TryRemove(userEmail, out _);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AppUser>> GetGroupMembersAsync(string groupName, CancellationToken ct = default)
        {
            if (!_groupMembers.TryGetValue(groupName, out var members))
            {
                return Task.FromResult<IReadOnlyList<AppUser>>(Array.Empty<AppUser>());
            }
            var users = members.Keys.Select(id => _users.TryGetValue(id, out var u) ? u : new AppUser { Id = id, Email = id, DisplayName = id }).ToList();
            return Task.FromResult<IReadOnlyList<AppUser>>(users);
        }

        public Task<bool> IsUserInGroupAsync(string userEmail, string groupName, CancellationToken ct = default)
        {
            var inGroup = _groupMembers.TryGetValue(groupName, out var members) && members.ContainsKey(userEmail);
            return Task.FromResult(inGroup);
        }

        private SecurityGroup UpdateCount(SecurityGroup g)
        {
            var count = _groupMembers.TryGetValue(g.Name, out var members) ? members.Count : 0;
            return new SecurityGroup { Id = g.Id, Name = g.Name, Description = g.Description, MemberCount = count };
        }
    }
}