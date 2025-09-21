using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Hosted service that verifies required security groups exist at application startup.
    /// It can ensure global groups, tenant-level groups, and optionally road-level groups,
    /// based on configuration and the current tenant/road configuration.
    /// </summary>
    internal sealed class SecurityGroupStartupVerifier : IHostedService
    {
        private readonly ILogger<SecurityGroupStartupVerifier> _logger;
        private readonly ISecurityGroupService _securityGroups;
        private readonly ITenantManagerService _tenants;
        private readonly IConfiguration _config;

        public SecurityGroupStartupVerifier(
            ILogger<SecurityGroupStartupVerifier> logger,
            ISecurityGroupService securityGroups,
            ITenantManagerService tenants,
            IConfiguration config)
        {
            _logger = logger;
            _securityGroups = securityGroups;
            _tenants = tenants;
            _config = config;
        }

        /// <summary>
        /// Ensures all configured security groups exist in the directory.
        /// This runs once during application startup.
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                bool ensureTenant = _config.GetValue<bool?>("SecurityGroups:EnsureTenantGroupsOnStartup") ?? true;
                bool ensureRoads  = _config.GetValue<bool?>("SecurityGroups:EnsureRoadGroupsOnStartup")  ?? false;

                // 1) Explicit list from configuration (optional)
                var requiredFromConfig = _config.GetSection("SecurityGroups:Required").Get<string[]>() ?? Array.Empty<string>();

                // 2) Always ensure Global Admins
                var groupsToEnsure = new HashSet<string>(requiredFromConfig, StringComparer.OrdinalIgnoreCase)
                {
                    SecurityGroupNameBuilder.GlobalAdminsGroup
                };

                // 3) Ensure all tenant-level groups
                if (ensureTenant)
                {
                    foreach (var tenant in _tenants.GetTenants())
                    {
                        groupsToEnsure.Add(SecurityGroupNameBuilder.TenantManager(tenant.TenantId));
                        groupsToEnsure.Add(SecurityGroupNameBuilder.TenantUser(tenant.TenantId));

                        if (ensureRoads)
                        {
                            foreach (var road in tenant.Roads)
                            {
                                groupsToEnsure.Add(SecurityGroupNameBuilder.TenantRoadUser(tenant.TenantId, road.RoadId));
                            }
                        }
                    }
                }

                // 4) Ensure in directory
                foreach (var name in groupsToEnsure)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    try
                    {
                        await _securityGroups.EnsureGroupAsync(name, description: null, cancellationToken);
                        _logger.LogInformation("Ensured security group: {GroupName}", name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed ensuring group {GroupName}", name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Security group verification failed at startup.");
            }
        }

        /// <summary>
        /// No-op on shutdown. Part of the <see cref="IHostedService"/> contract.
        /// </summary>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}