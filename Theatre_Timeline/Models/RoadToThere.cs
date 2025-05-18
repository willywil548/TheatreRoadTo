using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Models
{
    /// <summary>
    /// Describes the journey to take.
    /// </summary>
    public class RoadToThere : IRoadToThere
    {
        private static readonly object _lock = new();

        private int duration = -1;

        /// <summary>
        /// Gets for sets the start of the road.
        /// </summary>
        public DateTime? StartTime { get; set; } = DateTime.Now;

        /// <summary>
        /// Gets or set the end of the road.
        /// </summary>
        public DateTime? EndTime { get; set; } = DateTime.Now.AddDays(30);

        /// <summary>
        /// Gets or sets the banner.
        /// </summary>
        public string Banner { get; set; } = string.Empty;

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

        /// <summary>
        /// Gets or sets the road scope.
        /// </summary>
        public RoadScope RoadScope { get; set; } = RoadScope.Year;

        /// <summary>
        /// Gets or sets the length of the road.
        /// </summary>
        public int RoadScopeLength { get; set; } = 1;

        /// <summary>
        /// Gets the duration of the road.
        /// </summary>
        public int Duration
        {
            get
            {
                lock (_lock)
                {
                    if (this.duration == -1)
                    {
                        if (this.StartTime == null || this.EndTime == null)
                        {
                            this.duration = 1;
                        }
                        else
                        {
                            TimeSpan timeSpan = (DateTime)this.EndTime - (DateTime)this.StartTime;
                            switch (this.RoadScope)
                            {
                                // Drop to the next lower measurement.
                                // Used to break the road into blocks.
                                case RoadScope.Year:
                                case RoadScope.Month:
                                case RoadScope.Week:
                                    this.duration = (int)timeSpan.TotalDays;
                                    break;
                                case RoadScope.Day:
                                    this.duration = (int)timeSpan.TotalHours;
                                    break;
                                default:
                                    this.duration = 1;
                                    break;
                            }
                        }
                    }
                }

                return this.duration;
            }
        }

        public Address[] Addresses { get; set; } = Array.Empty<Address>();

        public override string ToString()
        {
            return $"{Title} - {this.StartTime?.ToString("dd-MMM-yy")} --> {this.EndTime?.ToString("dd-MMM-yy")}";
        }
    }
}