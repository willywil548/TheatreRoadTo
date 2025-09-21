using System.Diagnostics.CodeAnalysis;

namespace Theatre_TimeLine.Contracts
{
    public sealed class AppUser: IEquatable<AppUser>
    {
        public string Id { get; init; } = string.Empty;          // Provider-specific id (Graph objectId); may be email for stub
        public string DisplayName { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;

        public bool Equals(AppUser? other)
        {
            if (this is null || other is null)
            {
                return false;
            }

            return string.Equals(
                this.ToString(),
                other.ToString(),
                StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString()
        {
            return $"{this.Id}.{this.DisplayName}.{this.Email}";
        }
    }

    public sealed class AppUserComparer : IEqualityComparer<AppUser>
    {
        public bool Equals(AppUser? x, AppUser? y)
        {
            if (x is null)
            {
                return false;
            }

            return x.Equals(y);
        }

        public int GetHashCode([DisallowNull] AppUser obj)
        {
            return obj.ToString().GetHashCode(StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class SecurityGroup
    {
        public string Id { get; init; } = Guid.NewGuid().ToString();
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public int MemberCount { get; init; }
    }
}