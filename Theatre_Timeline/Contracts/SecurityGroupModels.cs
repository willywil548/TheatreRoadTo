namespace Theatre_TimeLine.Contracts
{
    public sealed class AppUser
    {
        public string Id { get; init; } = string.Empty;          // Provider-specific id (Graph objectId); may be email for stub
        public string DisplayName { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
    }

    public sealed class SecurityGroup
    {
        public string Id { get; init; } = Guid.NewGuid().ToString();
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public int MemberCount { get; init; }
    }
}