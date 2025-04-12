using Theatre_TimeLine.Models;

namespace Theatre_Timeline.Contracts
{
    public interface IRoadToThereManager
    {
        void SaveRoad(RoadToThere roadToThere);

        void RemoveRoad(Guid roadId);
    }
}
