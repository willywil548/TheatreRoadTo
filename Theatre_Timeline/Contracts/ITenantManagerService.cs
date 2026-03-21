using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Contracts
{
    /// <summary>
    /// Service interface for managing tenants and their configuration.
    /// </summary>
    public interface ITenantManagerService : IRoadToThereManager
    {
        /// <summary>
        /// Gets the path to the data folder relative to the web root (wwwroot) using
        /// forward slashes. Use this when constructing web-accessible URLs for tenant assets.
        /// </summary>
        string RelativeDataPath { get; }

        /// <summary>
        /// Gets the web-data path (relative to wwwroot) for building URLs to static assets.
        /// This value is safe to concatenate into URLs and uses forward slashes.
        /// </summary>
        string WebDataPath { get; }

        /// <summary>
        /// Gets the root file system path for a tenant's data.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <returns>The absolute path to the tenant's root directory.</returns>
        string GetTenantRootPath(Guid tenantId);

        /// <summary>
        /// Creates a new tenant and persists its configuration.
        /// </summary>
        /// <param name="tenant">The tenant container to create.</param>
        void CreateTenant(ITenantContainer tenant);

        /// <summary>
        /// Gets all configured tenants.
        /// </summary>
        /// <returns>An array of tenant containers.</returns>
        ITenantContainer[] GetTenants();

        /// <summary>
        /// Gets a tenant by its ID or by a road ID that belongs to it.
        /// </summary>
        /// <param name="guid">The tenant ID or road ID.</param>
        /// <returns>The tenant container if found; otherwise null.</returns>
        ITenantContainer? GetTenant(Guid guid);

        /// <summary>
        /// Removes a tenant and all its associated data.
        /// </summary>
        /// <param name="guid">The tenant ID to remove.</param>
        void RemoveTenant(Guid guid);

        /// <summary>
        /// Appends an address to a road under a synchronized tenant write operation.
        /// </summary>
        /// <param name="tenantId">The tenant that owns the road.</param>
        /// <param name="roadId">The road to append the address to.</param>
        /// <param name="address">The address to append.</param>
        /// <returns><see langword="true"/> when appended; otherwise <see langword="false"/>.</returns>
        bool TryAppendAddressToRoad(Guid tenantId, Guid roadId, Address address);
    }
}