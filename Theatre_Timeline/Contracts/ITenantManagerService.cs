namespace Theatre_Timeline.Contracts
{
    public interface ITenantManagerService : IRoadToThereManager
    {
        void CreateTenant(ITenantContainer tenant);

        ITenantContainer[] GetWebApps();

        ITenantContainer? GetTenant(Guid guid);

        void RemoveTenant(Guid guid);
    }
}