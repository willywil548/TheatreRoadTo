namespace Theatre_TimeLine.Contracts
{
    public interface ITenantManagerService
    {
        void CreateTenant(ITenantContainer tenant);

        ITenantContainer[] GetWebApps();

        ITenantContainer GetTenant(Guid guid);
    }
}