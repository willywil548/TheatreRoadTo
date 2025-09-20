using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Collections.Frozen;
using System.Security.Cryptography.Xml;
using System.Threading;
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
    internal sealed class GraphSecurityGroupService : ISecurityGroupService, IHostedService
    {
        private readonly ManualResetEventSlim graphUserEventWait = new ManualResetEventSlim(true);
        private readonly GraphServiceClient graphClient;
        private readonly string inviteRedirectUrl;
        private readonly ILogger logger;
        private readonly Dictionary<string, string> groupIdByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<AppUser>> groupUsersById = new Dictionary<string, HashSet<AppUser>>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan securityRefreshCycle = TimeSpan.FromMinutes(15);
        private readonly Timer securityGroupRefreshTimer;
        private DateTime lastCheckedGroups = DateTime.MinValue;


        /// <summary>
        /// Initializes a new instance of <see cref="GraphSecurityGroupService"/>.
        /// </summary>
        /// <param name="graph">An authenticated <see cref="GraphServiceClient"/> (app-only).</param>
        /// <param name="config">Application configuration used for invite settings.</param>
        public GraphSecurityGroupService(GraphServiceClient graph, IConfiguration config, ILogger<GraphSecurityGroupService> logger)
        {
            this.graphClient = graph;
            this.inviteRedirectUrl = config["Graph:InviteRedirectUrl"] ?? "https://localhost/";
            this.logger = logger;
            this.securityGroupRefreshTimer = new Timer(this.GetAndCacheAppSecurityGroups, this, -1, -1);
        }

        #region IHostedService Interface

        /// <summary>
        /// Runs startup activities.
        /// Syncs with Graph provider the current available security groups for the application.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await GetAndCacheAppSecurityGroups(this, cancellationToken);
                this.securityGroupRefreshTimer.Change(this.securityRefreshCycle, this.securityRefreshCycle);
            }
            catch (Exception e)
            {
                this.logger.LogError(e, "Failed to cache existing groups.");
                throw;
            }
        }

        /// <inheritDoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            this.graphClient.Dispose();
            return Task.CompletedTask;
        }

        #endregion

        /// <summary>
        /// Gets all application-scoped security groups (filtered by the app prefix).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Readonly collection of groups with member counts.</returns>
        public async Task<IReadOnlyList<SecurityGroup>> ListGroupsAsync(CancellationToken ct = default)
        {
            var resp = await graphClient.Groups.GetAsync(cfg =>
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

            var created = await graphClient.Groups.PostAsync(group, cancellationToken: ct);

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
            var id = ResolveGroupIdByName(groupName, ct);
            if (id == null) return;
            await graphClient.Groups[id].DeleteAsync(requestConfiguration: null, cancellationToken: ct);
            groupIdByName.Remove(groupName);
        }

        /// <summary>
        /// Searches users by email/UPN prefix (case-insensitive).
        /// </summary>
        /// <param name="query">Prefix of mail, UPN, or otherMails.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Matching users (best-effort mapped email/display name).</returns>
        public async Task<IReadOnlyList<AppUser>> SearchUsersAsync(string query, CancellationToken ct = default)
        {
            var resp = await graphClient.Users.GetAsync(cfg =>
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
                _ = await graphClient.Invitations.PostAsync(new Invitation
                {
                    InvitedUserEmailAddress = email,
                    InviteRedirectUrl = inviteRedirectUrl,
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
            var groupId = ResolveGroupIdByName(groupName, ct) ?? (await EnsureGroupAsync(groupName, ct: ct)).Id;
            var user = await ResolveUserByEmailAsync(userEmail, ct)
                       ?? await InviteUserAsync(userEmail, userEmail, null, ct);

            await graphClient.Groups[groupId].Members.Ref.PostAsync(new ReferenceCreate
            {
                OdataId = $"{graphClient.RequestAdapter.BaseUrl}/directoryObjects/{user.Id}"
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
            var groupId = ResolveGroupIdByName(groupName, ct);
            if (string.IsNullOrEmpty(groupId))
            {
                return;
            }

            if (!(await ResolveUserByEmailAsync(userEmail, ct) is AppUser appUser))
            {
                return;
            }

            await graphClient.Groups[groupId].Members[appUser.Id].Ref.DeleteAsync(requestConfiguration: null, cancellationToken: ct);
        }

        /// <summary>
        /// Retrieves all user members of a group by its display name.
        /// </summary>
        /// <param name="groupName">Display name of the group.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Readonly list of users in the group.</returns>
        public async Task<IReadOnlyList<AppUser>> GetGroupMembersAsync(string groupName, CancellationToken ct = default)
        {
            var groupId = ResolveGroupIdByName(groupName, ct);
            if (string.IsNullOrEmpty(groupId) ||
                !this.groupUsersById.TryGetValue(groupId, out HashSet<AppUser>? result) ||
                result is null)
            {
                return Array.Empty<AppUser>();
            }

            return await Task.FromResult(new List<AppUser>(result));
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
            var groupId = ResolveGroupIdByName(groupName, ct);
            if (string.IsNullOrEmpty(groupId))
            {
                return false;
            }

            var user = await ResolveUserByEmailAsync(userEmail, ct);
            if (user == null)
            {
                return false;
            }

            var members = await GetGroupMembersAsync(groupName, ct);
            return members.Any(m => string.Equals(m.Id, user.Id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves a group id from its display name, using an in-memory cache when possible.
        /// </summary>
        private string ResolveGroupIdByName(string groupName, CancellationToken ct)
        {
            this.graphUserEventWait.Wait(ct);

            if (groupIdByName.TryGetValue(groupName, out var id))
            {
                return id;
            }

            return string.Empty;
        }

        private async Task<AppUser?> ResolveUserByEmailAsync(string userEmail, CancellationToken ct)
        {
            this.graphUserEventWait.Wait(ct);

            foreach (HashSet<AppUser> set in this.groupUsersById.Values)
            {
                if (set.FirstOrDefault(app => string.Equals(userEmail, app.Email, StringComparison.OrdinalIgnoreCase)) is AppUser result)
                {
                    return await Task.FromResult(result);
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a group by display name using Graph (Top=1).
        /// </summary>
        private async Task<Group?> FindGroupByNameAsync(string groupName, CancellationToken ct)
        {
            try
            {
                var resp = await graphClient.Groups.GetAsync(cfg =>
                {
                    cfg.QueryParameters.Select = ["id", "displayName", "description"];
                    cfg.QueryParameters.Filter = $"displayName eq '{EscapeOData(groupName)}'";
                    cfg.QueryParameters.Top = 1;
                }, ct);
                return resp?.Value?.FirstOrDefault();
            }
            catch (Exception e)
            {
                this.logger.LogError(e, $"Failed to find the group:{groupName}");
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
                var members = await graphClient.Groups[groupId].Members.GetAsync(cfg =>
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
        private void CacheGroupId(string? name, string? id)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id))
            {
                return;
            }

            if (!groupIdByName.TryAdd(name, id))
            {
                this.logger.LogWarning($"Group name ({name}) already has a value ({this.groupIdByName[name]}) so could not add {id} ");
            }
        }

        private async void GetAndCacheAppSecurityGroups(object? state)
        {
            await GetAndCacheAppSecurityGroups(state, CancellationToken.None);
        }

        private async Task GetAndCacheAppSecurityGroups(object? state, CancellationToken cancellationToken)
        {
            if (state == null)
            {
                return;
            }

            this.graphUserEventWait.Reset();
            try
            {
                var resp = await graphClient.Groups.GetAsync(cfg =>
                {
                    cfg.QueryParameters.Select = ["id", "displayName", "description"];
                    cfg.QueryParameters.Filter = $"startswith(displayName,'{SecurityGroupNameBuilder.AppGroupPrefix}')";
                    cfg.QueryParameters.Expand = ["Members"];
                }, cancellationToken);

                if (resp?.Value == null)
                {
                    return;
                }

                foreach (var securityGroup in resp.Value)
                {
                    if (securityGroup == null || securityGroup.Id == null)
                    {
                        continue;
                    }

                    this.CacheGroupId(securityGroup?.DisplayName, securityGroup?.Id);

                    if (securityGroup?.Members == null)
                    {
                        continue;
                    }

                    // The only point to caching the group is for access to members.
                    HashSet<AppUser>? users = null;
                    if (!this.groupUsersById.TryGetValue(securityGroup.Id, out users))
                    {
                        users = new HashSet<AppUser>(new AppUserComparer());
                    }

                    if (users is null)
                    {
                        continue;
                    }

                    foreach (var member in securityGroup.Members)
                    {
                        if (!(member is User user) || string.IsNullOrEmpty(user.Mail))
                        {
                            continue;
                        }

                        users.Add(
                            new AppUser
                            {
                                Id = user.Id ?? user.Mail,
                                DisplayName = user.DisplayName ?? user.Mail,
                                Email = user.Mail
                            });
                    }

                    this.groupUsersById[securityGroup.Id] = users;
                }

                this.lastCheckedGroups = DateTime.UtcNow;
                this.logger.LogInformation("Cached security groups:");
                foreach (KeyValuePair<string, string> groupsPairs in this.groupIdByName)
                {
                    this.logger.LogInformation($"{groupsPairs.Key}:...");
                }
            }
            finally
            {
                this.graphUserEventWait.Set();
            }
        }
    }
}