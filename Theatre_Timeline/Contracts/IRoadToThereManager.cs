namespace Theatre_Timeline.Contracts
{
    public interface IRoadToThereManager
    {
        void SaveRoad(IRoadToThere roadToThere);

        void RemoveRoad(Guid roadId);

        IRoadToThere GetRoad(Guid roadId);
    }
}
