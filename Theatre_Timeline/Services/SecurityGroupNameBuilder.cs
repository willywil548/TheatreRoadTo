namespace Theatre_TimeLine.Services
{
    public enum RequiredSecurityLevel
    {
        Global,
        TenantManager,
        TenantUser,
        RoadUser
    }

    public static class SecurityGroupNameBuilder
    {
        /// <summary>
        /// Application prefix.
        /// </summary>
        public const string AppGroupPrefix = "Roads-";

        /// <summary>
        /// Global Admin Group.
        /// </summary>
        public static string GlobalAdminsGroup => $"{AppGroupPrefix}Admin";

        /// <summary>
        /// Tenant level manager.
        /// </summary>
        /// <param name="tenantId">Tenant ID.</param>
        /// <returns></returns>
        public static string TenantManager(Guid tenantId) => $"{AppGroupPrefix}Tenant-Manager-{tenantId}";

        /// <summary>
        /// Tenant User.
        /// </summary>
        /// <param name="tenantId">Tenant ID.</param>
        /// <returns></returns>
        public static string TenantUser(Guid tenantId) => $"{AppGroupPrefix}Tenant-User-{tenantId}";

        /// <summary>
        /// Road user.
        /// </summary>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="roadId">Road ID</param>
        /// <returns></returns>
        public static string TenantRoadUser(Guid tenantId, Guid roadId) => $"{AppGroupPrefix}Tenant-User-{tenantId}-{roadId}";
    }
}