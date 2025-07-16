using System.Text.Json;
using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Models
{
    public static class AddressExtensions
    {
        /// <summary>
        /// Checks if the address is a poll.
        /// </summary>
        /// <param name="address">The address to check.</param>
        /// <returns>True if the address is a poll, otherwise false.</returns>
        public static bool IsPoll(this IAddress? address)
        {
            if (address == null)
            {
                return false;
            }

            return address.AddressType == AddressType.Survey;
        }

        /// <summary>
        /// Checks if the address is a poll with content.
        /// </summary>
        /// <param name="address">The address to check.</param>
        /// <returns>True if the address is a poll with content, otherwise false.</returns>
        public static bool IsPollWithContent(this IAddress? address)
        {
            if (address == null)
            {
                return false;
            }

            return address.IsPoll() && !string.IsNullOrEmpty(address.Content);
        }

        public static bool TryGetPoll(this IAddress address, out Poll poll)
        {
            Poll? result = null;
            poll = new Poll();
            if (!address.IsPollWithContent())
            {
                return false;
            }

            try
            {
                result = JsonSerializer.Deserialize<Poll>(address.Content);
            }
            catch (JsonException)
            {
                Console.WriteLine("Failed to deserialize the poll content.");
            }

            // Assign the deserialized poll to the out parameter if successful
            if (result != null)
            {
                poll = result;
            }

            return result != null;
        }

        /// <summary>
        /// Retrieves the poll associated with the specified address.
        /// </summary>
        /// <param name="address">The address from which to retrieve the poll.</param>
        /// <returns>The <see cref="Poll"/> associated with the specified address.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the address does not contain a valid poll.</exception>
        public static Poll GetPoll(this IAddress? address)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address), "Address cannot be null.");
            }

            if (!address.TryGetPoll(out var poll))
            {
                throw new InvalidOperationException("The address does not contain a valid poll.");
            }

            return poll;
        }

        public static void SetPoll(this IAddress address, Poll? poll)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address), "Address cannot be null.");
            }

            if (poll == null)
            {
                address.Content = string.Empty;
                return;
            }

            if (address.AddressType != AddressType.Survey)
            {
                throw new InvalidOperationException("The address type is not a poll.");
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

            address.Content = JsonSerializer.Serialize(poll);
        }
    }
}
