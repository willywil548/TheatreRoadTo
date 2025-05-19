using System.Text.Json;
using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Models
{
    /// <summary>
    /// Represents a polling address.
    /// </summary>
    public class PollAddress : Address
    {
        public PollAddress()
        {
            this.AddressType = AddressType.Polling;
        }

        /// <summary>
        /// Get the poll.
        /// </summary>
        /// <returns><see cref="Poll"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown if address is not a Poll type.</exception>
        public Poll GetPoll()
        {
            if (this.AddressType != AddressType.Polling)
            {
                throw new InvalidOperationException("The address type is not a poll.");
            }

            if (string.IsNullOrEmpty(this.Content))
            {
                return new Poll();
            }

            return JsonSerializer.Deserialize<Poll>(this.Content) ?? new Poll();
        }

        /// <summary>
        /// Sets the poll for the address.
        /// </summary>
        /// <param name="poll">The Poll to set.</param>
        /// <exception cref="ArgumentException">Thrown if the question or options aren't set.</exception>
        /// <exception cref="InvalidOperationException"></exception>
        public void SetPollType(Poll? poll)
        {
            if (this.AddressType != AddressType.Polling)
            {
                throw new InvalidOperationException("The address type is not a poll.");
            }

            if (poll == null)
            {
                return;
            }

            if (poll.PollType == PollType.YesNo)
            {
                poll.Options =
                [
                    "Yes",
                    "No"
                ];
            }

            this.Content = JsonSerializer.Serialize(poll);
        }

        /// <summary>
        /// Sets the poll for the address.
        /// </summary>
        /// <param name="poll">The Poll to set.</param>
        /// <exception cref="ArgumentException">Thrown if the question or options aren't set.</exception>
        /// <exception cref="InvalidOperationException"></exception>
        public void SetPollQuestion(Poll? poll)
        {
            if (this.AddressType != AddressType.Polling)
            {
                throw new InvalidOperationException("The address type is not a poll.");
            }

            if (poll == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(poll.Question))
            {
                throw new ArgumentException("Poll question cannot be null or empty.", nameof(poll.Question));
            }

            this.Content = JsonSerializer.Serialize(poll);
        }

        /// <summary>
        /// Sets the poll for the address.
        /// </summary>
        /// <param name="poll">The Poll to set.</param>
        /// <exception cref="ArgumentException">Thrown if the question or options aren't set.</exception>
        /// <exception cref="InvalidOperationException"></exception>
        public void SetPollOptions(Poll? poll)
        {
            if (this.AddressType != AddressType.Polling)
            {
                throw new InvalidOperationException("The address type is not a poll.");
            }

            if (poll == null)
            {
                return;
            }

            if (poll.Options == null || !poll.Options.Any())
            {
                throw new ArgumentException("Poll options cannot be null or empty.", nameof(poll.Options));
            }

            this.Content = JsonSerializer.Serialize(poll);
        }

        /// <summary>
        /// Sets the poll for the address.
        /// </summary>
        /// <param name="poll">The Poll to set.</param>
        /// <exception cref="ArgumentException">Thrown if the question or options aren't set.</exception>
        /// <exception cref="InvalidOperationException"></exception>
        public void SetPoll(Poll? poll)
        {
            if (this.AddressType != AddressType.Polling)
            {
                throw new InvalidOperationException("The address type is not a poll.");
            }

            if (poll == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(poll.Question))
            {
                throw new ArgumentException("Poll question cannot be null or empty.", nameof(poll.Question));
            }

            if (poll.PollType == PollType.YesNo)
            {
                poll.Options =
                [
                    "Yes",
                    "No"
                ];
            }

            if (poll.Options == null || !poll.Options.Any())
            {
                throw new ArgumentException("Poll options cannot be null or empty.", nameof(poll.Options));
            }

            this.Content = JsonSerializer.Serialize(poll);
        }
    }
}
