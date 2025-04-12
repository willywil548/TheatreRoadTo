using System.Diagnostics;
using System.Text.Json;
using Theatre_Timeline.Contracts;
using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// This class handles tenant management and storage of information related to the tenant.
    /// </summary>
    internal sealed class TenantManagerService : ITenantManagerService
    {
        private const string configurationKey = "TenantManager:DataPath";
        private const string tenantConfigurationFile = "TenantConfiguration.json";
        private readonly string dataPath;

        /// <summary>
        /// Initializes a new instance of <see cref="TenantManagerService"/>
        /// </summary>
        /// <param name="configuration"></param>
        public TenantManagerService(IConfiguration configuration)
        {
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
            Directory.CreateDirectory(this.dataPath);
        }

        public void CreateTenant(ITenantContainer tenant)
        {
            string tenantPath = Path.Combine(this.dataPath, tenant.TenantId.ToString());
            FileInfo tenantConfigurationFileInfo = new(Path.Combine(tenantPath, tenantConfigurationFile));
            if (tenantConfigurationFileInfo.Exists)
            {
                tenantConfigurationFileInfo.Delete();
            }

            tenantConfigurationFileInfo.Directory?.Create();
            string tenantConfig = JsonSerializer.Serialize(tenant);
            File.WriteAllText(tenantConfigurationFileInfo.FullName, tenantConfig);
        }

        public void RemoveTenant(Guid guid)
        {
            string tenantPath = Path.Combine(this.dataPath, guid.ToString());
            DirectoryInfo tenantDirectory = new(tenantPath);
            if (tenantDirectory.Exists)
            {
                tenantDirectory.Delete(recursive: true);
            }
        }

        public void SaveRoad(RoadToThere roadToThere)
        {
            this.ActionRoad(roadToThere.TenantId, tenant => tenant.SaveRoad(roadToThere));
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
            ITenantContainer[] tenantContainers = this.GetWebApps();
            return tenantContainers.FirstOrDefault(c => c.TenantId.Equals(guid))
                ?? tenantContainers.FirstOrDefault(c => c.Roads.Any(r => r.RoadId.Equals(guid)));
        }

        public ITenantContainer[] GetWebApps()
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
            ITenantContainer? tenant = this.GetTenant(tenantId);
            if (tenant == null)
            {
                return;
            }

            action?.Invoke(tenant);
        }
    }
}