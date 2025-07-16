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
            this.AddressType = AddressType.Survey;
        }
    }
}
