namespace Theatre_TimeLine.Contracts
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
        /// Gets the roads under the tenant.
        /// </summary>
        IRoadToThere[] Roads { get; }
    }
}