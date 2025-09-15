using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    public static class SecurityGroupNameBuilder
    {
        public const string AppGroupPrefix = "Roads-";
        public static string GlobalAdminsGroup => $"{AppGroupPrefix}Admin";

        // "Roads-Tenant-Manager-<tenantId>"
        public static string TenantManager(Guid tenantId) => $"{AppGroupPrefix}Tenant-Manager-{tenantId}";

        // "Roads-Tenant-User-<tenantId>"
        public static string TenantUser(Guid tenantId) => $"{AppGroupPrefix}Tenant-User-{tenantId}";

        // "Roads-Tenant-User-<tenantId>-<roadId>"
        public static string TenantRoadUser(Guid tenantId, Guid roadId) => $"{AppGroupPrefix}Tenant-User-{tenantId}-{roadId}";
    }
}