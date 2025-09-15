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
        public const string DemoGuid = "00000000-0000-0000-0000-3eca75185852";
        private static readonly SemaphoreSlim writeManager = new(1, 1);
        private const string configurationKey = "TenantManager:DataPath";
        private const string tenantConfigurationFile = "TenantConfiguration.json";
        private readonly string dataPath;
        private readonly ISecurityGroupService? _securityGroups;

        /// <summary>
        /// Initializes a new instance of <see cref="TenantManagerService"/>
        /// </summary>
        /// <param name="configuration"></param>
        /// <param name="securityGroups">Optional: security group service to ensure groups upon tenant/road creation.</param>
        public TenantManagerService(IConfiguration configuration, ISecurityGroupService? securityGroups = null)
        {
            this._securityGroups = securityGroups;

            string? dataPath = configuration.GetValue<string>(configurationKey);
            if (string.IsNullOrEmpty(dataPath))
            {
                dataPath = "./webapps/data";
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

        public void CreateTenant(ITenantContainer tenant)
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

        public void RemoveTenant(Guid guid)
        {
            DirectoryInfo tenantDirectory = new(this.GetTenantRootPath(guid));
            if (tenantDirectory.Exists)
            {
                tenantDirectory.Delete(recursive: true);
            }
        }

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

        public void RemoveRoad(Guid roadId)
        {
            this.ActionRoad(roadId, tenant => tenant.RemoveRoad(roadId));
        }

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

        public ITenantContainer? GetTenant(Guid guid)
        {
            ITenantContainer[] tenantContainers = this.GetTenants();
            return tenantContainers.FirstOrDefault(c => c.TenantId.Equals(guid))
                ?? tenantContainers.FirstOrDefault(c => c.Roads.Any(r => r.RoadId.Equals(guid)));
        }

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

        private void CreateDemoPage()
        {
            // Setup the Demo.
            ITenantContainer tenant = new TenantContainer
            {
                TenantName = "Demo",
                Description = "Demo Road to highlight some capabilities.",
                AdminSecurityGroup = "Demo-RoadToThere",
                TenantId = Guid.Parse(DemoGuid),
                TenantPath = Path.Combine(this.dataPath, DemoGuid)
            };

            this.CreateTenant(tenant);

            IRoadToThere roadToThere = new RoadToThere
            {
                RoadId = Guid.Parse(DemoGuid),
                Description = "Road from start to finish",
                TenantId = tenant.TenantId,
                EndTime = DateTime.Now.AddDays(365),
                Title = "Full Demo of capabilities",
                RoadAdmin = tenant.AdminSecurityGroup,
            };

            this.SaveRoad(roadToThere);
        }

        public string GetTenantRootPath(Guid tenantId)
        {
            return Path.Combine(this.dataPath, tenantId.ToString());
        }
    }
}