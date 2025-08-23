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

        /// <summary>
        /// Sets the poll type for the specified address.
        /// </summary>
        /// <remarks>If the <paramref name="poll"/> is of type <see cref="PollType.YesNo"/>, the options
        /// are automatically set to "Yes" and "No". The poll is serialized to JSON and stored in the address
        /// content.</remarks>
        /// <param name="address">The address to which the poll type will be set. Cannot be <see langword="null"/>.</param>
        /// <param name="poll">The poll object containing the poll type and options. If <see langword="null"/>, the address content is
        /// cleared.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="address"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the <paramref name="address"/> is not of type <see cref="AddressType.Survey"/>.</exception>
        public static void SetPollType(this IAddress address, Poll? poll)
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

            if (poll.PollType == PollType.YesNo)
            {
                poll.Options =
                [
                    "Yes",
                    "No"
                ];
            }

            address.Content = JsonSerializer.Serialize(poll);
        }

        /// <summary>
        /// Sets the poll question for the specified address if the address type is a survey.
        /// </summary>
        /// <remarks>The method serializes the <paramref name="poll"/> object and assigns it to the
        /// content of the address.</remarks>
        /// <param name="address">The address to which the poll question will be set. Must not be <see langword="null"/> and must have an
        /// address type of <see cref="AddressType.Survey"/>.</param>
        /// <param name="poll">The poll containing the question to set. If <see langword="null"/>, the method returns without making
        /// changes.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="address"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the <paramref name="address"/> does not have an address type of <see cref="AddressType.Survey"/>.</exception>
        /// <exception cref="ArgumentException">Thrown if the <paramref name="poll"/> question is <see langword="null"/> or empty.</exception>
        public static void SetPollQuestion(this IAddress? address, Poll? poll)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address), "Address cannot be null.");
            }

            if (address.AddressType != AddressType.Survey)
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

            address.Content = JsonSerializer.Serialize(poll);
        }

        /// <summary>
        /// Sets the poll options for the specified address if the address type is a poll.
        /// </summary>
        /// <param name="address">The address to set the poll options for. Must not be <see langword="null"/> and must have an address type of
        /// <see cref="AddressType.Survey"/>.</param>
        /// <param name="poll">The poll containing the options to set. If <see langword="null"/>, the method returns without making
        /// changes. The poll options must not be <see langword="null"/> or empty.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="address"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the <paramref name="address"/> does not have an address type of <see cref="AddressType.Survey"/>.</exception>
        /// <exception cref="ArgumentException">Thrown if <paramref name="poll"/> has <see langword="null"/> or empty options.</exception>
        public static void SetPollOptions(this IAddress? address, Poll? poll)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address), "Address cannot be null.");
            }

            if (address.AddressType != AddressType.Survey)
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

            address.Content = JsonSerializer.Serialize(poll);
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
