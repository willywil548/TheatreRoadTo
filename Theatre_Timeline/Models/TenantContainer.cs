using Theatre_Timeline.Contracts;
using System.ComponentModel;
using System.Text.Json;
using System.Diagnostics;

namespace Theatre_TimeLine.Models
{
    public sealed class TenantContainer : ITenantContainer
    {
        private const string RoadToThereFileName = "RoadConfiguration.json";

        /// <summary>
        /// Gets or sets the TenantName.
        /// </summary>
        [DisplayName("Tenant Name")]
        public string TenantName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the TenantDescription.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the Tenant ID.
        /// </summary>
        [DisplayName("Tenant ID")]
        public Guid TenantId { get; set; } = Guid.Empty;

        /// <summary>
        /// Gets or sets the admin security group for the tenant.
        /// </summary>
        [Browsable(false)]
        public string AdminSecurityGroup { get; set; } = string.Empty;

        /// <summary>
        /// Gets or the roads.
        /// </summary>
        public IRoadToThere[] Roads => GetRoads();

        /// <summary>
        /// Gets or sets the tenant path.
        /// </summary>
        [Browsable(false)]
        public string? TenantPath { get; set; }

        public void SaveRoad(RoadToThere roadToThere)
        {
            if (string.IsNullOrEmpty(this.TenantPath))
            {
                return;
            }

            FileInfo fileInfo = new FileInfo(Path.Combine(this.TenantPath, roadToThere.RoadId.ToString(), RoadToThereFileName));
            fileInfo.Directory?.Create();
            File.WriteAllText(fileInfo.FullName, JsonSerializer.Serialize(roadToThere));
        }

        public void RemoveRoad(Guid roadId)
        {
            if (string.IsNullOrEmpty(this.TenantPath))
            {
                return;
            }

            DirectoryInfo roadDirectory = new(Path.Combine(this.TenantPath, roadId.ToString()));
            if (roadDirectory.Exists)
            {
                roadDirectory.Delete(recursive: true);
            }
        }

        public IRoadToThere GetRoad(Guid roadId)
        {
            if (string.IsNullOrEmpty(this.TenantPath))
            {
                throw new InvalidOperationException("Tenant path is not set.");
            }

            string roadPath = Path.Combine(this.TenantPath, roadId.ToString(), RoadToThereFileName);
            if (File.Exists(roadPath))
            {
                IRoadToThere? road = JsonSerializer.Deserialize<RoadToThere>(File.ReadAllText(roadPath));
                if (road != null)
                {
                    return road;
                }
            }

            throw new FileNotFoundException("Road not found.", roadPath);
        }

        /// <summary>
        /// Gets the roads configured for the tenant.
        /// </summary>
        /// <returns>A collection of roads.</returns>
        private IRoadToThere[] GetRoads()
        {
            List<IRoadToThere> roadToTheres = [];
            if (string.IsNullOrEmpty(this.TenantPath))
            {
                return [.. roadToTheres];
            }

            foreach (string roadDir in Directory.GetDirectories(this.TenantPath, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    DirectoryInfo roadDirectory = new(roadDir);
                    roadToTheres.Add(this.GetRoad(Guid.Parse(roadDirectory.Name)));
                }
                catch
                {
                    Trace.WriteLine($"Failed to get road:{roadDir}");
                }
            }

            return [.. roadToTheres];
        }
    }
}