using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Models
{
    public class Address : IAddress, IEquatable<IAddress>
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

        public string GetDisplayLocation()
        {
            return this.Location.HasValue ? this.Location.Value.ToString("MMM dd @ HH:mm") : "No Date";
        }

        /// <summary>
        /// Returns a string representation of the object, including its title, description, address type, location, and
        /// content.
        /// </summary>
        /// <returns>A string that concatenates the title, description, address type, location, and content of the object,
        /// separated by hyphens.</returns>
        public override string ToString()
        {
            return $"{this.Title} - {this.Description} - {this.AddressType} - {this.Location} - {this.Content}";
        }

        /// <summary>
        /// Determines whether the specified <see cref="IAddress"/> is equal to the current instance.
        /// </summary>
        /// <remarks>The comparison is case-insensitive and based on the string representation of the
        /// addresses.</remarks>
        /// <param name="other">The <see cref="IAddress"/> to compare with the current instance.</param>
        /// <returns><see langword="true"/> if the specified <see cref="IAddress"/> is equal to the current instance; otherwise,
        /// <see langword="false"/>.</returns>
        public bool Equals(IAddress? other)
        {
            if (ReferenceEquals(other, null))
            {
                return false;
            }

            return string.Equals(this?.ToString(), other?.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
