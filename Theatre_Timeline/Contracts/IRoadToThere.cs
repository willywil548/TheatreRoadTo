namespace Theatre_TimeLine.Contracts
{
    public enum RoadScope
    {
        Month = 0,
        Day = 1,
        Week = 2,
        Year = 3,
    }

    public interface IRoadToThere
    {
        /// <summary>
        /// Gets or sets the Start time of the road.
        /// </summary>
        DateTime? StartTime { get; set; }

        /// <summary>
        /// Gets or sets the End time of the road.
        /// </summary>
        DateTime? EndTime { get; set; }

        /// <summary>
        /// Gets or sets the Banner of the road.
        /// </summary>
        string Banner { get; set; }

        /// <summary>
        /// Gets the Tenant ID.
        /// </summary>
        Guid TenantId { get; }

        /// <summary>
        /// Gets the road ID.
        /// </summary>
        Guid RoadId { get; }

        /// <summary>
        /// Gets or sets the road admin.
        /// </summary>
        string RoadAdmin { get; set; }

        /// <summary>
        /// Gets or sets css for a road.
        /// </summary>
        string PageHostCssPath { get; set; }

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        string Description { get; set; }

        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        string Title { get; set; }

        /// <summary>
        /// Gets or sets the road scope.
        /// </summary>
        RoadScope RoadScope { get; set; }

        /// <summary>
        /// Gets or sets the length of the scope.
        /// </summary>
        int RoadScopeLength { get; set; }

        /// <summary>
        /// Gets the duration of the road.
        /// </summary>
        int Duration { get; }

        /// <summary>
        /// Gets the address.
        /// </summary>
        /// <returns>An array of <see cref="IAddress"/>.</returns>
        IAddress[] Addresses { get; set; }
    }
}