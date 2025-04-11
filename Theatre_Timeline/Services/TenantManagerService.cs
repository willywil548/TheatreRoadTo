using System.Text.Json;
using System.Text.Json.Serialization;
using Theatre_TimeLine.Contracts;

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

        public ITenantContainer GetTenant(Guid guid)
        {
            throw new NotImplementedException();
        }

        public ITenantContainer[] GetWebApps()
        {
            throw new NotImplementedException();
        }
    }
}