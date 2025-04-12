using System.ComponentModel;

namespace Theatre_Timeline.Contracts
{
    public interface IRoadToThere
    {
        /// <summary>
        /// Gets the Start time of the road.
        /// </summary>
        DateTimeOffset StartTime { get; }

        /// <summary>
        /// Gets the End time of the road.
        /// </summary>
        DateTimeOffset EndTime { get; }

        /// <summary>
        /// Gets the Banner of the road.
        /// </summary>
        string Banner { get; }

        /// <summary>
        /// Gets the Tenant ID.
        /// </summary>
        Guid TenantId { get; }

        /// <summary>
        /// Gets the road ID.
        /// </summary>
        Guid RoadId { get; }

        /// <summary>
        /// Gets the road admin.
        /// </summary>
        string RoadAdmin { get; }

        [DisplayName("URI to pull css from.")]
        string PageHostCssPath { get; }

        /// <summary>
        /// Gets the description.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the title.
        /// </summary>
        string Title { get; }
    }
}