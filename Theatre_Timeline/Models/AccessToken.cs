using System.Text.Json.Serialization;

namespace Theatre_TimeLine.Models
{
    /// <summary>
    /// Represents a token-based access grant for external users.
    /// Allows users to access specific roads without AAD membership.
    /// </summary>
    public class AccessToken
    {
        /// <summary>
        /// Unique identifier for this access token record.
        /// </summary>
        [JsonPropertyName("tokenId")]
        public Guid TokenId { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Hashed version of the token for secure storage.
        /// The plaintext token is only shown once during creation.
        /// </summary>
        [JsonPropertyName("tokenHash")]
        public string TokenHash { get; set; } = string.Empty;

        /// <summary>
        /// Student/user display name for management purposes.
        /// </summary>
        [JsonPropertyName("studentName")]
        public string StudentName { get; set; } = string.Empty;

        /// <summary>
        /// Email address for sending the access link.
        /// </summary>
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// The tenant this access token belongs to.
        /// </summary>
        [JsonPropertyName("tenantId")]
        public Guid TenantId { get; set; }

        /// <summary>
        /// Roads the user is authorized to access.
        /// </summary>
        [JsonPropertyName("authorizedRoadIds")]
        public List<Guid> AuthorizedRoadIds { get; set; } = new();

        /// <summary>
        /// When this token was created.
        /// </summary>
        [JsonPropertyName("createdUtc")]
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Email of the manager who created this invitation.
        /// </summary>
        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>
        /// Whether this token is currently active.
        /// </summary>
        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// When the token was revoked (if applicable).
        /// </summary>
        [JsonPropertyName("revokedUtc")]
        public DateTime? RevokedUtc { get; set; }

        /// <summary>
        /// Who revoked the token (if applicable).
        /// </summary>
        [JsonPropertyName("revokedBy")]
        public string? RevokedBy { get; set; }

        /// <summary>
        /// Last time this token was used to access the site.
        /// </summary>
        [JsonPropertyName("lastAccessedUtc")]
        public DateTime? LastAccessedUtc { get; set; }

        /// <summary>
        /// Number of times this token has been used.
        /// </summary>
        [JsonPropertyName("accessCount")]
        public int AccessCount { get; set; }

        /// <summary>
        /// The last road the user accessed - for returning to where they left off.
        /// </summary>
        [JsonPropertyName("lastAccessedRoadId")]
        public Guid? LastAccessedRoadId { get; set; }

        /// <summary>
        /// Optional notes from the manager about this user.
        /// </summary>
        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Result of creating a new access token.
    /// Contains the plaintext token which is only available at creation time.
    /// </summary>
    public class AccessTokenCreationResult
    {
        /// <summary>
        /// Whether the creation was successful.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Error message if creation failed.
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// The created access token record.
        /// </summary>
        public AccessToken? Token { get; set; }

        /// <summary>
        /// The plaintext token to include in the access URL.
        /// This is only available at creation time - store/send it immediately.
        /// </summary>
        public string PlaintextToken { get; set; } = string.Empty;

        /// <summary>
        /// The full access URL to send to the user.
        /// Built by the calling component using NavigationManager.
        /// </summary>
        public string AccessUrl { get; set; } = string.Empty;

        /// <summary>
        /// Creates a successful result with the token and plaintext token.
        /// </summary>
        /// <param name="token">The created access token.</param>
        /// <param name="plaintextToken">The plaintext token for the access URL.</param>
        /// <returns>A successful <see cref="AccessTokenCreationResult"/>.</returns>
        public static AccessTokenCreationResult Succeeded(AccessToken token, string plaintextToken) => new()
        {
            Success = true,
            Token = token,
            PlaintextToken = plaintextToken,
            AccessUrl = string.Empty // Caller sets this using NavigationManager
        };

        /// <summary>
        /// Creates a failed result with an error message.
        /// </summary>
        /// <param name="error">The error message describing why creation failed.</param>
        /// <returns>A failed <see cref="AccessTokenCreationResult"/>.</returns>
        public static AccessTokenCreationResult Failed(string error) => new()
        {
            Success = false,
            ErrorMessage = error
        };
    }

    /// <summary>
    /// Result of validating an access token.
    /// </summary>
    public class AccessTokenValidationResult
    {
        /// <summary>
        /// Whether the token is valid and active.
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// The access token if valid.
        /// </summary>
        public AccessToken? Token { get; set; }

        /// <summary>
        /// Reason for validation failure.
        /// </summary>
        public string? FailureReason { get; set; }

        /// <summary>
        /// Creates a successful validation result.
        /// </summary>
        /// <param name="token">The validated access token.</param>
        /// <returns>A successful <see cref="AccessTokenValidationResult"/>.</returns>
        public static AccessTokenValidationResult Success(AccessToken token) => new()
        {
            IsValid = true,
            Token = token
        };

        /// <summary>
        /// Creates a failed validation result.
        /// </summary>
        /// <param name="reason">The reason for validation failure.</param>
        /// <returns>A failed <see cref="AccessTokenValidationResult"/>.</returns>
        public static AccessTokenValidationResult Failure(string reason) => new()
        {
            IsValid = false,
            FailureReason = reason
        };
    }
}
