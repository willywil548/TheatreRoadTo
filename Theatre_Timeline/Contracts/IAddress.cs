namespace Theatre_TimeLine.Contracts
{
    /// <summary>
    /// Describes the type of address.
    /// </summary>
    public enum AddressType
    {
        Notification = 0,
        SocialMedia = 1,
        VideoMedia = 2,
        Polling = 3,
    }

    /// <summary>
    /// Describes an address.
    /// </summary>
    public interface IAddress
    {
        /// <summary>
        /// Gets or sets the location.
        /// </summary>
        DateTime? Location { get; set; }

        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        string Title { get; set; }

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        string Description { get; set; }

        /// <summary>
        /// Gets or sets the content.
        /// </summary>
        string Content { get; set; }

        /// <summary>
        /// Gets or sets the address type.
        /// </summary>
        AddressType AddressType { get; set; }

        /// <summary>
        /// Gets or sets the tags.
        /// </summary>
        IEnumerable<string> Tags { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to delay the release of the address.
        /// </summary>
        bool DelayRelease { get; set; }
    }
}
