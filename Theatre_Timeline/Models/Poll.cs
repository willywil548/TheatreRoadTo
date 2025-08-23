namespace Theatre_TimeLine.Models
{
    /// <summary>
    /// Represents the type of poll.
    /// </summary>
    public enum PollType
    {
        YesNo,
        MultipleChoice
    }

    /// <summary>
    /// Represents a poll.
    /// </summary>
    public class Poll
    {
        /// <summary>
        /// Gets or sets the unique identifier for the poll.
        /// </summary>
        public Guid PollId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Gets or sets the title of the poll.
        /// </summary>
        public string Question { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the description of the poll.
        /// </summary>
        public List<string> Options { get; set; } = [];

        /// <summary>
        /// Gets or sets the content of the poll.
        /// </summary>
        public PollType PollType { get; set; } = PollType.MultipleChoice;

        /// <summary>
        /// Gets or sets the tags associated with the poll.
        /// </summary>
        public List<string> Responses { get; set; } = new List<string>();
    }
}
