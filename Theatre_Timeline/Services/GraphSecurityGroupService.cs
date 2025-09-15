using Microsoft.Graph;
using Microsoft.Graph.Models;
using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Microsoft Graph-based implementation of <see cref="ISecurityGroupService"/>.
    /// Provides CRUD-lite operations for security groups and memberships, user lookup, and guest invitation.
    /// </summary>
    /// <remarks>
    /// Notes:
    /// - Uses application permissions via <see cref="GraphServiceClient"/>.
    /// - Some Graph endpoints are eventually consistent (e.g., invitations); short delays may be observed.
    /// - Group mail nicknames are sanitized to comply with Graph constraints.
    /// </remarks>
    internal sealed class GraphSecurityGroupService : ISecurityGroupService
    {
        private readonly GraphServiceClient _graph;
        private readonly string _inviteRedirectUrl;
        private readonly Dictionary<string, string> _groupIdByName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of <see cref="GraphSecurityGroupService"/>.
        /// </summary>
        /// <param name="graph">An authenticated <see cref="GraphServiceClient"/> (app-only).</param>
        /// <param name="config">Application configuration used for invite settings.</param>
        public GraphSecurityGroupService(GraphServiceClient graph, IConfiguration config)
        {
            _graph = graph;
            _inviteRedirectUrl = config["Graph:InviteRedirectUrl"] ?? "https://localhost/";
        }

        /// <summary>
        /// Gets all application-scoped security groups (filtered by the app prefix).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Readonly collection of groups with member counts.</returns>
        public async Task<IReadOnlyList<SecurityGroup>> ListGroupsAsync(CancellationToken ct = default)
        {
            var resp = await _graph.Groups.GetAsync(cfg =>
            {
                cfg.QueryParameters.Select = ["id", "displayName", "description"];
                cfg.QueryParameters.Top = 999;
                cfg.QueryParameters.Filter = $"startswith(displayName,'{SecurityGroupNameBuilder.AppGroupPrefix}')";
            }, ct);

            var items = resp?.Value ?? new List<Group>();
            var result = new List<SecurityGroup>(items.Count);
            foreach (var g in items)
            {
                var count = await TryCountMembersAsync(g.Id!, ct);
                result.Add(new SecurityGroup { Id = g.Id!, Name = g.DisplayName ?? string.Empty, Description = g.Description, MemberCount = count });
                CacheGroupId(g.DisplayName ?? string.Empty, g.Id!);
            }
            return result;
        }

        /// <summary>
        /// Gets a group by display name.
        /// </summary>
        /// <param name="groupName">Display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Group info or null if not found.</returns>
        public async Task<SecurityGroup?> GetGroupByNameAsync(string groupName, CancellationToken ct = default)
        {
            var g = await FindGroupByNameAsync(groupName, ct);
            if (g == null) return null;
            var count = await TryCountMembersAsync(g.Id!, ct);
            return new SecurityGroup { Id = g.Id!, Name = g.DisplayName ?? string.Empty, Description = g.Description, MemberCount = count };
        }

        /// <summary>
        /// Ensures a security group exists by name; creates it if missing.
        /// </summary>
        /// <param name="groupName">Display name of the group to ensure.</param>
        /// <param name="description">Optional group description.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The ensured group metadata.</returns>
        public async Task<SecurityGroup> EnsureGroupAsync(string groupName, string? description = null, CancellationToken ct = default)
        {
            var existing = await FindGroupByNameAsync(groupName, ct);
            if (existing != null)
            {
                CacheGroupId(groupName, existing.Id!);
                var count = await TryCountMembersAsync(existing.Id!, ct);
                return new SecurityGroup { Id = existing.Id!, Name = existing.DisplayName ?? string.Empty, Description = existing.Description, MemberCount = count };
            }

            var sanitized = SanitizeMailNickname(groupName);
            var safeDescription = SanitizeDescription(description);

            var group = new Group
            {
                DisplayName = groupName,
                MailEnabled = false,
                SecurityEnabled = true,
                GroupTypes = new List<string>(),
                MailNickname = sanitized
            };
            if (safeDescription != null)
                group.Description = safeDescription;

            var created = await _graph.Groups.PostAsync(group, cancellationToken: ct);

            if (created == null || string.IsNullOrEmpty(created.Id))
                throw new InvalidOperationException("Failed to create group in Entra ID.");

            CacheGroupId(groupName, created.Id!);
            return new SecurityGroup { Id = created.Id!, Name = created.DisplayName ?? groupName, Description = created.Description, MemberCount = 0 };
        }

        /// <summary>
        /// Sanitizes the description to remove control characters and limit length.
        /// </summary>
        private static string? SanitizeDescription(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return null;

            // Remove control chars and limit length to Graph constraints (defensive: 1024)
            var clean = new string(description.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
            if (clean.Length > 1024) clean = clean[..1024];

            return string.IsNullOrWhiteSpace(clean) ? null : clean;
        }

        /// <summary>
        /// Deletes a group (if it exists) by its display name.
        /// </summary>
        /// <param name="groupName">Display name of the group to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task DeleteGroupByNameAsync(string groupName, CancellationToken ct = default)
        {
            var id = await ResolveGroupIdByName(groupName, ct);
            if (id == null) return;
            await _graph.Groups[id].DeleteAsync(requestConfiguration: null, cancellationToken: ct);
            _groupIdByName.Remove(groupName);
        }

        /// <summary>
        /// Searches users by email/UPN prefix (case-insensitive).
        /// </summary>
        /// <param name="query">Prefix of mail, UPN, or otherMails.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Matching users (best-effort mapped email/display name).</returns>
        public async Task<IReadOnlyList<AppUser>> SearchUsersAsync(string query, CancellationToken ct = default)
        {
            var resp = await _graph.Users.GetAsync(cfg =>
            {
                cfg.QueryParameters.Top = 20;
                cfg.QueryParameters.Select = ["id", "displayName", "mail", "userPrincipalName"];
                cfg.QueryParameters.Filter = $"startswith(mail,'{EscapeOData(query)}') or startswith(userPrincipalName,'{EscapeOData(query)}') or otherMails/any(c: startswith(c,'{EscapeOData(query)}'))";
            }, ct);

            return (resp?.Value ?? new List<User>()).Select(ToAppUser).ToList();
        }

        /// <summary>
        /// Invites a user by email if they do not exist and optionally adds them to groups.
        /// </summary>
        /// <param name="email">User email address.</param>
        /// <param name="displayName">Optional display name for the invite.</param>
        /// <param name="groups">Optional list of group display names to add after invite/resolve.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The invited or existing user.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the user cannot be resolved after invitation.</exception>
        public async Task<AppUser> InviteUserAsync(string email, string? displayName, IEnumerable<string>? groups = null, CancellationToken ct = default)
        {
            var user = await ResolveUserByEmailAsync(email, ct);
            if (user == null)
            {
                _ = await _graph.Invitations.PostAsync(new Invitation
                {
                    InvitedUserEmailAddress = email,
                    InviteRedirectUrl = _inviteRedirectUrl,
                    SendInvitationMessage = true,
                },
                requestConfiguration: null,
                cancellationToken: ct);

                user = await ResolveUserByEmailAsync(email, ct);
                if (user == null)
                    throw new InvalidOperationException("User invitation sent but the user record could not be resolved.");
            }

            if (groups != null)
            {
                foreach (var g in groups)
                {
                    await AddUserToGroupAsync(email, g, ct);
                }
            }

            return user;
        }

        /// <summary>
        /// Adds a user to a group by email and group display name (ensures the group exists).
        /// </summary>
        /// <param name="userEmail">Email of the user to add.</param>
        /// <param name="groupName">Display name of the target group.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task AddUserToGroupAsync(string userEmail, string groupName, CancellationToken ct = default)
        {
            var groupId = await ResolveGroupIdByName(groupName, ct) ?? (await EnsureGroupAsync(groupName, ct: ct)).Id;
            var user = await ResolveUserByEmailAsync(userEmail, ct)
                       ?? await InviteUserAsync(userEmail, userEmail, null, ct);

            await _graph.Groups[groupId].Members.Ref.PostAsync(new ReferenceCreate
            {
                OdataId = $"{_graph.RequestAdapter.BaseUrl}/directoryObjects/{user.Id}"
            }, requestConfiguration: null, cancellationToken: ct);
        }

        /// <summary>
        /// Removes a user from a group by email and group display name.
        /// </summary>
        /// <param name="userEmail">Email of the user to remove.</param>
        /// <param name="groupName">Display name of the target group.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task RemoveUserFromGroupAsync(string userEmail, string groupName, CancellationToken ct = default)
        {
            var groupId = await ResolveGroupIdByName(groupName, ct);
            if (groupId == null) return;

            var user = await ResolveUserByEmailAsync(userEmail, ct);
            if (user == null) return;

            await _graph.Groups[groupId].Members[user.Id].Ref.DeleteAsync(requestConfiguration: null, cancellationToken: ct);
        }

        /// <summary>
        /// Retrieves all user members of a group by its display name.
        /// </summary>
        /// <param name="groupName">Display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Readonly list of users in the group.</returns>
        public async Task<IReadOnlyList<AppUser>> GetGroupMembersAsync(string groupName, CancellationToken ct = default)
        {
            var groupId = await ResolveGroupIdByName(groupName, ct);
            if (groupId == null) return Array.Empty<AppUser>();

            var result = new List<AppUser>(32);

            var page = await _graph.Groups[groupId].Members.GetAsync(cfg =>
            {
                cfg.QueryParameters.Top = 999;
            }, ct);

            while (true)
            {
                if (page?.Value != null)
                {
                    foreach (var item in page.Value)
                    {
                        if (item is User u)
                            result.Add(ToAppUser(u));
                    }
                }

                var nextLink = page?.OdataNextLink;
                if (string.IsNullOrEmpty(nextLink)) break;

                // WithUrl(...) GetAsync in this SDK shape does not take a CancellationToken
                page = await _graph.Groups[groupId].Members.WithUrl(nextLink).GetAsync();
            }

            return result;
        }

        /// <summary>
        /// Checks whether a user is a member of the given group.
        /// </summary>
        /// <param name="userEmail">Email of the user to check.</param>
        /// <param name="groupName">Display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the user is in the group; otherwise false.</returns>
        public async Task<bool> IsUserInGroupAsync(string userEmail, string groupName, CancellationToken ct = default)
        {
            var groupId = await ResolveGroupIdByName(groupName, ct);
            if (groupId == null) return false;

            var user = await ResolveUserByEmailAsync(userEmail, ct);
            if (user == null) return false;

            var members = await GetGroupMembersAsync(groupName, ct);
            return members.Any(m => string.Equals(m.Id, user.Id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves a group id from its display name, using an in-memory cache when possible.
        /// </summary>
        private async Task<string?> ResolveGroupIdByName(string groupName, CancellationToken ct)
        {
            if (_groupIdByName.TryGetValue(groupName, out var id)) return id;
            var g = await FindGroupByNameAsync(groupName, ct);
            if (g?.Id == null) return null;
            CacheGroupId(groupName, g.Id);
            return g.Id;
        }

        /// <summary>
        /// Finds a group by display name using Graph (Top=1).
        /// </summary>
        private async Task<Group?> FindGroupByNameAsync(string groupName, CancellationToken ct)
        {
            try
            {
                var resp = await _graph.Groups.GetAsync(cfg =>
                {
                    cfg.QueryParameters.Select = ["id", "displayName", "description"];
                    cfg.QueryParameters.Filter = $"displayName eq '{EscapeOData(groupName)}'";
                    cfg.QueryParameters.Top = 1;
                }, ct);
                return resp?.Value?.FirstOrDefault();
            }
            catch (Exception e)
            {
                Console.Write(e);
                throw;
            }
        }

        /// <summary>
        /// Attempts to count group members efficiently using <c>@odata.count</c> with eventual consistency.
        /// </summary>
        private async Task<int> TryCountMembersAsync(string groupId, CancellationToken ct)
        {
            try
            {
                var members = await _graph.Groups[groupId].Members.GetAsync(cfg =>
                {
                    cfg.QueryParameters.Top = 1;
                    cfg.QueryParameters.Count = true;
                    cfg.Headers.Add("ConsistencyLevel", "eventual");
                }, ct);

                if (members?.AdditionalData != null &&
                    members.AdditionalData.TryGetValue("@odata.count", out var raw))
                {
                    return raw switch
                    {
                        int i => i,
                        long l => (int)l,
                        _ => 0
                    };
                }

                return members?.Value?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Maps a Graph <see cref="User"/> to <see cref="AppUser"/> selecting a best-effort email.
        /// </summary>
        private static AppUser ToAppUser(User u)
        {
            var email = !string.IsNullOrWhiteSpace(u.Mail) ? u.Mail :
                        (u.OtherMails?.FirstOrDefault() ?? u.UserPrincipalName ?? string.Empty);
            return new AppUser
            {
                Id = u.Id ?? email,
                Email = email,
                DisplayName = u.DisplayName ?? email
            };
        }

        /// <summary>
        /// Resolves a user by email via direct lookup or filtered search across mail/UPN/otherMails.
        /// </summary>
        private async Task<AppUser?> ResolveUserByEmailAsync(string email, CancellationToken ct)
        {
            try
            {
                var direct = await _graph.Users[email].GetAsync(cfg =>
                {
                    cfg.QueryParameters.Select = ["id", "displayName", "mail", "userPrincipalName", "otherMails"];
                }, ct);
                if (direct != null) return ToAppUser(direct);
            }
            catch { /* ignore */ }

            var resp = await _graph.Users.GetAsync(cfg =>
            {
                cfg.QueryParameters.Select = ["id", "displayName", "mail", "userPrincipalName", "otherMails"];
                cfg.QueryParameters.Top = 1;
                cfg.QueryParameters.Filter =
                    $"mail eq '{EscapeOData(email)}' or userPrincipalName eq '{EscapeOData(email)}' or otherMails/any(c:c eq '{EscapeOData(email)}')";
            }, ct);

            var u = resp?.Value?.FirstOrDefault();
            return u != null ? ToAppUser(u) : null;
        }

        /// <summary>
        /// Produces a Graph-safe mail nickname from a display name.
        /// </summary>
        private static string SanitizeMailNickname(string name)
        {
            var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-').ToArray();
            var nick = new string(chars);
            if (nick.Length > 64) nick = nick[..64];
            if (nick.EndsWith("-")) nick = nick.TrimEnd('-');
            if (string.IsNullOrWhiteSpace(nick)) nick = "group";
            return nick;
        }

        /// <summary>
        /// Escapes single quotes for safe OData filter usage.
        /// </summary>
        private static string EscapeOData(string value) => value.Replace("'", "''");

        /// <summary>
        /// Caches a display-name to group-id mapping to reduce Graph lookups.
        /// </summary>
        private void CacheGroupId(string name, string id)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id))
                _groupIdByName[name] = id;
        }
    }
}