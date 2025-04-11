using Theatre_TimeLine.Contracts;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Runtime.Serialization;
using System.Text.Json;

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
        public string AdminSecurityGroup { get; set; } = string.Empty;

        /// <summary>
        /// Gets or the roads.
        /// </summary>
        [IgnoreDataMember]
        [JsonIgnore]
        public IRoadToThere[] Roads => GetRoads();

        /// <summary>
        /// Gets or sets the tenant path.
        /// </summary>
        [Browsable(false)]
        [IgnoreDataMember]
        [JsonIgnore]
        public string? TenantPath { get; set; }

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
                string roadPath = Path.Combine(roadDir, RoadToThereFileName);
                if (JsonSerializer.Deserialize<RoadToThere>(File.ReadAllText(roadPath)) is RoadToThere road)
                {
                    roadToTheres.Add(road);
                }
            }

            return [.. roadToTheres];
        }
    }
}