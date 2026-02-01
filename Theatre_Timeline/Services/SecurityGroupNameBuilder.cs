namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Specifies the required security level for accessing resources.
    /// </summary>
    [Flags]
    public enum RequiredSecurityLevel
    {
        /// <summary>
        /// Global administrator access.
        /// </summary>
        Global = 1,

        /// <summary>
        /// Tenant manager access.
        /// </summary>
        TenantManager = 2,

        /// <summary>
        /// Tenant user access.
        /// </summary>
        TenantUser = 4,

        /// <summary>
        /// Road-specific user access.
        /// </summary>
        RoadUser = 8,

        /// <summary>
        /// Not authorized for any access.
        /// </summary>
        NotAuthorized = 0
    }

    /// <summary>
    /// Utility class for building standardized security group names.
    /// </summary>
    public static class SecurityGroupNameBuilder
    {
        /// <summary>
        /// The prefix used for all application security groups.
        /// </summary>
        public const string AppGroupPrefix = "Roads-";

        /// <summary>
        /// Gets the global administrators group name.
        /// </summary>
        public static string GlobalAdminsGroup => $"{AppGroupPrefix}Admin";

        /// <summary>
        /// Gets the tenant manager group name for a specific tenant.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <returns>The tenant manager group name.</returns>
        public static string TenantManager(Guid tenantId) => $"{AppGroupPrefix}Tenant-Manager-{tenantId}";

        /// <summary>
        /// Gets the tenant user group name for a specific tenant.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <returns>The tenant user group name.</returns>
        public static string TenantUser(Guid tenantId) => $"{AppGroupPrefix}Tenant-User-{tenantId}";

        /// <summary>
        /// Gets the road user group name for a specific road within a tenant.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <param name="roadId">The road ID.</param>
        /// <returns>The road user group name.</returns>
        public static string TenantRoadUser(Guid tenantId, Guid roadId) => $"{AppGroupPrefix}Tenant-User-{tenantId}-{roadId}";
    }
}