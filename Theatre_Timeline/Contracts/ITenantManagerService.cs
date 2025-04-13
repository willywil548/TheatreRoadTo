namespace Theatre_TimeLine.Contracts
{
    public interface ITenantManagerService : IRoadToThereManager
    {
        void CreateTenant(ITenantContainer tenant);

        ITenantContainer[] GetWebApps();

        ITenantContainer? GetTenant(Guid guid);

        void RemoveTenant(Guid guid);
    }
}