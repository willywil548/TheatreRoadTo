using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Models
{
    public class Address : IAddress, IEquatable<Address>
    {
        /// <summary>
        /// Gets or sets the location.
        /// </summary>
        public DateTime? Location { get; set; } = DateTime.Now;

        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the content.
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the address type.
        /// </summary>
        public AddressType AddressType { get; set; } = AddressType.Notification;

        /// <summary>
        /// Gets or sets the tags.
        /// </summary>
        public IEnumerable<string> Tags { get; set; } = Enumerable.Empty<string>();

        /// <summary>
        /// Gets or sets the delay release.
        /// </summary>
        public bool DelayRelease { get; set; } = false;

        public override string ToString()
        {
            return $"{this.Title} - {this.Description} - {this.AddressType} - {this.Location} - {this.Content}";
        }

        public bool Equals(Address? other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return string.Equals(this?.ToString(), other?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
