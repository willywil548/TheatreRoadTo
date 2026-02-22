using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Models
{
    public static class EnumDisplayExtensions
    {
        public static string ToDisplayString(this PollType pollType)
        {
            return pollType switch
            {
                PollType.YesNo => "Yes / No",
                PollType.MultipleChoice => "Multiple Choice",
                _ => pollType.ToString()
            };
        }

        public static string ToDisplayString(this AddressType addressType)
        {
            return addressType switch
            {
                AddressType.Notification => "Notification",
                AddressType.Video => "Video",
                AddressType.Survey => "Survey",
                _ => addressType.ToString()
            };
        }
    }
}
