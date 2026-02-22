using System.Diagnostics;
using System.Text.Json;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// This class handles tenant management and storage of information related to the tenant.
    /// </summary>
    internal sealed class TenantManagerService : ITenantManagerService
    {
        /// <summary>
        /// The default GUID used for the demo tenant if not configured.
        /// </summary>
        public const string DefaultDemoGuid = "00000000-0000-0000-0000-3eca75185852";

        /// <summary>
        /// Gets the configured demo tenant GUID.
        /// </summary>
        public string DemoTenantId { get; }

        private const string HomeVariable = "%home%";
        private static readonly SemaphoreSlim writeManager = new(1, 1);
        private const string dataPathConfigKey = "TenantManager:DataPath";
        private const string demoTenantIdConfigKey = "TenantManager:DemoTenantId";
        private const string tenantConfigurationFile = "TenantConfiguration.json";
        private readonly string dataPath;
        private readonly ISecurityGroupService? _securityGroups;

        /// <summary>
        /// Initializes a new instance of <see cref="TenantManagerService"/>.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="securityGroups">Optional: security group service to ensure groups upon tenant/road creation.</param>
        public TenantManagerService(IConfiguration configuration, ISecurityGroupService? securityGroups = null)
        {
            this._securityGroups = securityGroups;

            // Read demo tenant ID from configuration or use default
            this.DemoTenantId = configuration.GetValue<string>(demoTenantIdConfigKey) ?? DefaultDemoGuid;

            string? dataPath = configuration.GetValue<string>(dataPathConfigKey);
            if (string.IsNullOrEmpty(dataPath))
            {
                dataPath = "./webapps/data";
            }

            if (dataPath.StartsWith(HomeVariable, StringComparison.OrdinalIgnoreCase))
            {
                string home = Environment.GetEnvironmentVariable("home") ?? ".";
                string homePath = Path.GetFullPath(home);
                dataPath = dataPath.Replace(home, string.Empty);
                dataPath = Path.Combine(homePath, dataPath.Trim(new char[] { '/', '\\' }));
            }

            if (!Path.IsPathRooted(dataPath))
            {
                dataPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    dataPath.Trim(['.', '\\', '/']));
            }

            this.dataPath = dataPath;
            if (!Directory.Exists(this.dataPath))
            {
                Directory.CreateDirectory(this.dataPath);

                // Create a demo page.
                CreateDemoPage();
            }
        }

        /// <inheritdoc />
        public void CreateTenant(ITenantContainer tenant)
        {
            writeManager.Wait();
            try
            {
                FileInfo tenantConfigurationFileInfo = new(
                    Path.Combine(
                        this.GetTenantRootPath(tenant.TenantId),
                        tenantConfigurationFile));
                if (tenantConfigurationFileInfo.Exists)
                {
                    tenantConfigurationFileInfo.Delete();
                }

                tenantConfigurationFileInfo.Directory?.Create();
                string tenantConfig = JsonSerializer.Serialize(tenant);
                File.WriteAllText(tenantConfigurationFileInfo.FullName, tenantConfig);

                // Optionally ensure tenant-level groups.
                if (this._securityGroups != null)
                {
                    _ = this._securityGroups.EnsureGroupAsync(SecurityGroupNameBuilder.TenantManager(tenant.TenantId));
                    _ = this._securityGroups.EnsureGroupAsync(SecurityGroupNameBuilder.TenantUser(tenant.TenantId));
                }
            }
            finally
            {
                writeManager.Release();
            }
        }

        /// <inheritdoc />
        public void RemoveTenant(Guid guid)
        {
            writeManager.Wait();
            try
            {
                DirectoryInfo tenantDirectory = new(this.GetTenantRootPath(guid));
                if (tenantDirectory.Exists)
                {
                    tenantDirectory.Delete(recursive: true);
                }
            }
            finally
            {
                writeManager.Release();
            }
        }

        /// <inheritdoc />
        public void SaveRoad(IRoadToThere? roadToThere)
        {
            if (roadToThere == null)
            {
                return;
            }

            this.ActionRoad(roadToThere.TenantId, tenant => tenant.SaveRoad(roadToThere));

            // Optionally ensure road-level group.
            if (this._securityGroups != null)
            {
                _ = this._securityGroups.EnsureGroupAsync(SecurityGroupNameBuilder.TenantRoadUser(roadToThere.TenantId, roadToThere.RoadId));
            }
        }

        /// <inheritdoc />
        public void RemoveRoad(Guid roadId)
        {
            this.ActionRoad(roadId, tenant => tenant.RemoveRoad(roadId));
        }

        /// <inheritdoc />
        public IRoadToThere GetRoad(Guid roadId)
        {
            ITenantContainer? tenant = this.GetTenant(roadId);
            if (tenant == null)
            {
                throw new InvalidOperationException("Tenant not found.");
            }

            return tenant.Roads.FirstOrDefault(r => r.RoadId.Equals(roadId))
                ?? throw new InvalidOperationException("Road not found.");
        }

        /// <inheritdoc />
        public ITenantContainer? GetTenant(Guid guid)
        {
            ITenantContainer[] tenantContainers = this.GetTenants();
            return tenantContainers.FirstOrDefault(c => c.TenantId.Equals(guid))
                ?? tenantContainers.FirstOrDefault(c => c.Roads.Any(r => r.RoadId.Equals(guid)));
        }

        /// <inheritdoc />
        public ITenantContainer[] GetTenants()
        {
            List<ITenantContainer> containers = [];

            // Get the web apps by folder.
            foreach (string dir in Directory.GetDirectories(this.dataPath, "*", SearchOption.TopDirectoryOnly))
            {
                FileInfo fileInfo = new FileInfo(Path.Combine(dir, tenantConfigurationFile));
                if (!fileInfo.Exists)
                {
                    continue;
                }

                // Gets the basic information about Tenants.
                try
                {
                    TenantContainer? tenant = JsonSerializer.Deserialize<TenantContainer>(File.ReadAllText(fileInfo.FullName));
                    if (tenant != null)
                    {
                        tenant.TenantPath = fileInfo.Directory?.FullName;
                        containers.Add(tenant);
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex.Message);
                }
            }

            return [.. containers];
        }

        /// <summary>
        /// Performs an action on a road within a tenant context with thread safety.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <param name="action">The action to perform on the tenant container.</param>
        private void ActionRoad(Guid tenantId, Action<ITenantContainer> action)
        {
            writeManager.Wait();
            try
            {
                ITenantContainer? tenant = this.GetTenant(tenantId);
                if (tenant == null)
                {
                    return;
                }

                action?.Invoke(tenant);
            }
            finally
            {
                writeManager.Release();
            }
        }

        /// <summary>
        /// Creates the demo page and tenant for first-time setup.
        /// </summary>
        private void CreateDemoPage()
        {
            // Setup the Demo.
            ITenantContainer tenant = new TenantContainer
            {
                TenantName = "Demo",
                Description = "Demo Road to highlight some capabilities.",
                AdminSecurityGroup = "Demo-RoadToThere",
                TenantId = Guid.Parse(DemoTenantId),
                TenantPath = Path.Combine(this.dataPath, DemoTenantId)
            };

            this.CreateTenant(tenant);

            IRoadToThere roadToThere = new RoadToThere
            {
                RoadId = Guid.Parse(DemoTenantId),
                Description = "Road from start to finish",
                TenantId = tenant.TenantId,
                EndTime = DateTime.Now.AddDays(365),
                Title = "Full Demo of capabilities",
                RoadAdmin = tenant.AdminSecurityGroup,
            };

            this.SaveRoad(roadToThere);
        }

        /// <inheritdoc />
        public string GetTenantRootPath(Guid tenantId)
        {
            return Path.Combine(this.dataPath, tenantId.ToString());
        }
    }
}