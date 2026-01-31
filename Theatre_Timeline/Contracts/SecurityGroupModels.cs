using System.Diagnostics.CodeAnalysis;

namespace Theatre_TimeLine.Contracts
{
    /// <summary>
    /// Represents an application user with identity information.
    /// </summary>
    public sealed class AppUser : IEquatable<AppUser>
    {
        /// <summary>
        /// Gets the provider-specific user ID (e.g., Graph objectId or email for stub).
        /// </summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Gets the user's display name.
        /// </summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>
        /// Gets the user's email address.
        /// </summary>
        public string Email { get; init; } = string.Empty;

        /// <summary>
        /// Determines whether this user equals another user.
        /// </summary>
        /// <param name="other">The other user to compare.</param>
        /// <returns>True if the users are equal; otherwise false.</returns>
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

        /// <summary>
        /// Returns a string representation of the user.
        /// </summary>
        /// <returns>A string combining Id, DisplayName, and Email.</returns>
        public override string ToString()
        {
            return $"{this.Id}.{this.DisplayName}.{this.Email}";
        }
    }

    /// <summary>
    /// Equality comparer for <see cref="AppUser"/> instances.
    /// </summary>
    public sealed class AppUserComparer : IEqualityComparer<AppUser>
    {
        /// <inheritdoc />
        public bool Equals(AppUser? x, AppUser? y)
        {
            if (x is null)
            {
                return false;
            }

            return x.Equals(y);
        }

        /// <inheritdoc />
        public int GetHashCode([DisallowNull] AppUser obj)
        {
            return obj.ToString().GetHashCode(StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Represents a security group in the identity provider.
    /// </summary>
    public sealed class SecurityGroup
    {
        /// <summary>
        /// Gets the unique identifier for the group.
        /// </summary>
        public string Id { get; init; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Gets the display name of the group.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Gets the optional description of the group.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Gets the number of members in the group.
        /// </summary>
        public int MemberCount { get; init; }
    }
}