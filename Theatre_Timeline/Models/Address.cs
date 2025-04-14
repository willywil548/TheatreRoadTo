using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Models
{
    public class Address : IAddress
    {
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
        public string Tags { get; set; } = string.Empty;
    }
}
