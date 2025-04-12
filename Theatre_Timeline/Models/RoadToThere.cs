using Theatre_Timeline.Contracts;

namespace Theatre_TimeLine.Models
{
    /// <summary>
    /// Describes the journey to take.
    /// </summary>
    public class RoadToThere : IRoadToThere
    {
        /// <summary>
        /// Gets for sets the start of the road.
        /// </summary>
        public DateTimeOffset StartTime { get; set; } = DateTimeOffset.Now;

        /// <summary>
        /// Gets or set the end of the road.
        /// </summary>
        public DateTimeOffset EndTime { get; set; } = DateTimeOffset.Now.AddDays(30);

        /// <summary>
        /// Gets or sets the banner.
        /// </summary>
        public string Banner { get; private set; } = string.Empty;

        /// <summary>
        /// Gets or sets the unique ID of the road.
        /// </summary>
        public Guid RoadId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Gets or sets the road admin security groups.
        /// </summary>
        /// <remarks>Expected to be csv format.</remarks>
        public string RoadAdmin { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the CSS URI to pull in the CSS from Tenant Host.
        /// </summary>
        public string PageHostCssPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets the Tenant ID.
        /// </summary>
        public Guid TenantId { get; set; }

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"{Title} - {this.StartTime.Date}-{this.EndTime.Date}";
        }
    }
}