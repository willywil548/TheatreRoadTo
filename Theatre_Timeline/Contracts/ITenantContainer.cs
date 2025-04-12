using System.Text.Json.Serialization;

namespace Theatre_Timeline.Contracts
{
    public interface ITenantContainer
    {
        /// <summary>
        /// Gets Tenant Name.
        /// </summary>
        string TenantName { get; }

        /// <summary>
        /// Gets the description.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the Tenant ID.
        /// </summary>
        Guid TenantId { get; }

        /// <summary>
        /// Gets the admin security groups for the tenant.
        /// </summary>
        string AdminSecurityGroup { get; }

        /// <summary>
        /// Gets the roads under the tenant.
        /// </summary>
        [JsonIgnore]
        IRoadToThere[] Roads { get; }
    }
}