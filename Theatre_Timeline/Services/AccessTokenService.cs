using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Service for managing token-based access for external users.
    /// </summary>
    public interface IAccessTokenService
    {
        /// <summary>
        /// Creates a new access token for a user.
        /// </summary>
        /// <param name="studentName">The display name of the student/user.</param>
        /// <param name="email">The email address for sending the access link.</param>
        /// <param name="tenantId">The tenant this token belongs to.</param>
        /// <param name="roadIds">The roads the user is authorized to access.</param>
        /// <param name="createdBy">The email of the manager creating this token.</param>
        /// <param name="notes">Optional notes about this user.</param>
        /// <returns>A result containing the created token and plaintext token for the URL.</returns>
        Task<AccessTokenCreationResult> CreateTokenAsync(
            string studentName,
            string email,
            Guid tenantId,
            IEnumerable<Guid> roadIds,
            string createdBy,
            string? notes = null);

        /// <summary>
        /// Validates a token and returns the associated access grant.
        /// </summary>
        /// <param name="token">The plaintext token from the access URL.</param>
        /// <returns>A result indicating whether the token is valid and the associated access token.</returns>
        Task<AccessTokenValidationResult> ValidateTokenAsync(string token);

        /// <summary>
        /// Records an access event for a token.
        /// </summary>
        /// <param name="tokenId">The unique identifier of the token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task RecordAccessAsync(Guid tokenId);

        /// <summary>
        /// Revokes an access token.
        /// </summary>
        /// <param name="tokenId">The unique identifier of the token to revoke.</param>
        /// <param name="revokedBy">The email of the manager revoking the token.</param>
        /// <returns><c>true</c> if the token was successfully revoked; otherwise <c>false</c>.</returns>
        Task<bool> RevokeTokenAsync(Guid tokenId, string revokedBy);

        /// <summary>
        /// Gets all tokens for a tenant.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <returns>A read-only list of access tokens for the tenant.</returns>
        Task<IReadOnlyList<AccessToken>> GetTokensForTenantAsync(Guid tenantId);

        /// <summary>
        /// Gets all tokens for a specific road.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <param name="roadId">The road identifier.</param>
        /// <returns>A read-only list of access tokens that grant access to the specified road.</returns>
        Task<IReadOnlyList<AccessToken>> GetTokensForRoadAsync(Guid tenantId, Guid roadId);

        /// <summary>
        /// Gets a specific token by ID.
        /// </summary>
        /// <param name="tokenId">The unique identifier of the token.</param>
        /// <returns>The access token if found; otherwise <c>null</c>.</returns>
        Task<AccessToken?> GetTokenByIdAsync(Guid tokenId);

        /// <summary>
        /// Updates road assignments for an existing token.
        /// </summary>
        /// <param name="tokenId">The unique identifier of the token.</param>
        /// <param name="roadIds">The new set of road IDs to assign.</param>
        /// <param name="updatedBy">The email of the manager making the update.</param>
        /// <returns><c>true</c> if the update was successful; otherwise <c>false</c>.</returns>
        Task<bool> UpdateRoadAssignmentsAsync(Guid tokenId, IEnumerable<Guid> roadIds, string updatedBy);

        /// <summary>
        /// Updates the last accessed road for a token.
        /// Called when user navigates between roads.
        /// </summary>
        /// <param name="tokenId">The unique identifier of the token.</param>
        /// <param name="roadId">The road ID the user navigated to.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task UpdateLastAccessedRoadAsync(Guid tokenId, Guid roadId);

        /// <summary>
        /// Regenerates a new plaintext token for an existing access token.
        /// The old token becomes invalid immediately.
        /// </summary>
        /// <param name="tokenId">The unique identifier of the token to regenerate.</param>
        /// <param name="regeneratedBy">The email of the manager regenerating the token.</param>
        /// <returns>A result containing the new plaintext token, or failure if the token doesn't exist or is revoked.</returns>
        Task<AccessTokenCreationResult> RegenerateTokenAsync(Guid tokenId, string regeneratedBy);

        /// <summary>
        /// Gets all active tokens associated with an email address.
        /// Supports parent/guardian scenarios where one email has multiple student tokens.
        /// </summary>
        /// <param name="email">The email address to search for.</param>
        /// <returns>A read-only list of active access tokens for the email.</returns>
        Task<IReadOnlyList<AccessToken>> GetTokensByEmailAsync(string email);
    }

    /// <summary>
    /// File-based implementation of access token service.
    /// </summary>
    public class AccessTokenService : IAccessTokenService
    {
        private const int TokenByteLength = 32; // 256-bit tokens
        private readonly string _storagePath;
        private readonly ILogger<AccessTokenService> _logger;
        private readonly IConfiguration _configuration;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // Thread-safe in-memory index for fast token lookup (hash -> tokenId)
        private ConcurrentDictionary<string, Guid>? _tokenHashIndex;
        private volatile bool _indexLoaded;

        public AccessTokenService(
            IConfiguration configuration,
            ILogger<AccessTokenService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            // Store tokens alongside tenant data using the same path configuration
            var basePath = configuration.GetValue<string>("TenantManager:DataPath") ?? "./data";
            
            // Handle %home% variable (same as TenantManagerService)
            const string homeVariable = "%home%";
            if (basePath.StartsWith(homeVariable, StringComparison.OrdinalIgnoreCase))
            {
                var home = Environment.GetEnvironmentVariable("home") ?? ".";
                var homePath = Path.GetFullPath(home);
                basePath = basePath.Replace(homeVariable, string.Empty, StringComparison.OrdinalIgnoreCase);
                basePath = Path.Combine(homePath, basePath.Trim('/', '\\'));
            }

            if (!Path.IsPathRooted(basePath))
            {
                basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, basePath.TrimStart('.', '/', '\\'));
            }

            // Tokens are stored inside each tenant's folder as {tenantId}/_tokens/{tokenId}.json
            _storagePath = basePath;

            _logger.LogInformation("Token storage base path: {Path}", _storagePath);
        }

        /// <inheritdoc />
        public async Task<AccessTokenCreationResult> CreateTokenAsync(
            string studentName,
            string email,
            Guid tenantId,
            IEnumerable<Guid> roadIds,
            string createdBy,
            string? notes = null)
        {
            if (string.IsNullOrWhiteSpace(studentName))
            {
                return AccessTokenCreationResult.Failed("Student name is required");
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                return AccessTokenCreationResult.Failed("Email is required");
            }

            // Validate email format
            if (!IsValidEmail(email))
            {
                return AccessTokenCreationResult.Failed("Invalid email format");
            }

            if (tenantId == Guid.Empty)
            {
                return AccessTokenCreationResult.Failed("Tenant ID is required");
            }

            var roadIdList = roadIds.ToList();
            if (roadIdList.Count == 0)
            {
                return AccessTokenCreationResult.Failed("At least one road must be assigned");
            }

            try
            {
                // Generate cryptographically secure token
                var tokenBytes = RandomNumberGenerator.GetBytes(TokenByteLength);
                var plaintextToken = Convert.ToBase64String(tokenBytes)
                    .Replace("+", "-")  // URL-safe
                    .Replace("/", "_")  // URL-safe
                    .TrimEnd('=');      // Remove padding

                var tokenHash = HashToken(plaintextToken);

                var accessToken = new AccessToken
                {
                    TokenId = Guid.NewGuid(),
                    TokenHash = tokenHash,
                    StudentName = studentName.Trim(),
                    Email = email.Trim().ToLowerInvariant(),
                    TenantId = tenantId,
                    AuthorizedRoadIds = roadIdList,
                    CreatedUtc = DateTime.UtcNow,
                    CreatedBy = createdBy,
                    IsActive = true,
                    Notes = notes
                };

                await SaveTokenAsync(accessToken);

                _logger.LogInformation(
                    "Created access token for {StudentName} (email hash: {EmailHash}) with access to {RoadCount} roads",
                    studentName,
                    HashEmailForLogging(email),
                    accessToken.AuthorizedRoadIds.Count);

                // Return token and plaintext - caller builds the full URL using NavigationManager
                return AccessTokenCreationResult.Succeeded(accessToken, plaintextToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create access token (email hash: {EmailHash})", HashEmailForLogging(email));
                return AccessTokenCreationResult.Failed("Failed to create access token");
            }
        }

        /// <inheritdoc />
        public async Task<AccessTokenValidationResult> ValidateTokenAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return AccessTokenValidationResult.Failure("Token is required");
            }

            var tokenHash = HashToken(token);

            await _lock.WaitAsync();
            try
            {
                await EnsureIndexLoadedAsync();

                if (_tokenHashIndex == null || !_tokenHashIndex.TryGetValue(tokenHash, out var tokenId))
                {
                    _logger.LogWarning("Token validation failed: token not found");
                    return AccessTokenValidationResult.Failure("Invalid token");
                }

                var accessToken = await LoadTokenByIdAsync(tokenId);
                if (accessToken == null)
                {
                    _logger.LogWarning("Token validation failed: token record missing for {TokenId}", tokenId);
                    return AccessTokenValidationResult.Failure("Token not found");
                }

                if (!accessToken.IsActive)
                {
                    _logger.LogWarning("Token validation failed: token revoked for {StudentName}", accessToken.StudentName);
                    return AccessTokenValidationResult.Failure("Token has been revoked");
                }

                _logger.LogInformation("Token validated successfully for {StudentName}", accessToken.StudentName);
                return AccessTokenValidationResult.Success(accessToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task RecordAccessAsync(Guid tokenId)
        {
            await _lock.WaitAsync();
            try
            {
                var token = await LoadTokenByIdAsync(tokenId);
                if (token != null)
                {
                    token.LastAccessedUtc = DateTime.UtcNow;
                    token.AccessCount++;
                    await SaveTokenInternalAsync(token);

                    _logger.LogInformation(
                        "Recorded access for {StudentName}, total access count: {Count}",
                        token.StudentName,
                        token.AccessCount);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<bool> RevokeTokenAsync(Guid tokenId, string revokedBy)
        {
            await _lock.WaitAsync();
            try
            {
                var token = await LoadTokenByIdAsync(tokenId);
                if (token == null)
                {
                    return false;
                }

                var oldTokenHash = token.TokenHash;
                token.IsActive = false;
                token.RevokedUtc = DateTime.UtcNow;
                token.RevokedBy = revokedBy;

                await SaveTokenInternalAsync(token);

                // Remove from index after successful save
                RemoveFromIndex(oldTokenHash);

                _logger.LogInformation(
                    "Token revoked for {StudentName} by {RevokedBy}",
                    token.StudentName,
                    revokedBy);

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AccessToken>> GetTokensForTenantAsync(Guid tenantId)
        {
            var tokens = await LoadTokensForTenantAsync(tenantId);
            return tokens
                .OrderByDescending(t => t.CreatedUtc)
                .ToList();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AccessToken>> GetTokensForRoadAsync(Guid tenantId, Guid roadId)
        {
            var tokens = await LoadTokensForTenantAsync(tenantId);
            return tokens
                .Where(t => t.AuthorizedRoadIds.Contains(roadId))
                .OrderByDescending(t => t.CreatedUtc)
                .ToList();
        }

        /// <inheritdoc />
        public async Task<AccessToken?> GetTokenByIdAsync(Guid tokenId)
        {
            return await LoadTokenByIdAsync(tokenId);
        }

        /// <inheritdoc />
        public async Task<bool> UpdateRoadAssignmentsAsync(Guid tokenId, IEnumerable<Guid> roadIds, string updatedBy)
        {
            await _lock.WaitAsync();
            try
            {
                var token = await LoadTokenByIdAsync(tokenId);
                if (token == null || !token.IsActive)
                {
                    return false;
                }

                token.AuthorizedRoadIds = roadIds.ToList();
                await SaveTokenInternalAsync(token);

                _logger.LogInformation(
                    "Updated road assignments for {StudentName}: {RoadCount} roads (by {UpdatedBy})",
                    token.StudentName,
                    token.AuthorizedRoadIds.Count,
                    updatedBy);

                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task UpdateLastAccessedRoadAsync(Guid tokenId, Guid roadId)
        {
            await _lock.WaitAsync();
            try
            {
                var token = await LoadTokenByIdAsync(tokenId);
                if (token != null && token.IsActive && token.AuthorizedRoadIds.Contains(roadId))
                {
                    token.LastAccessedRoadId = roadId;
                    await SaveTokenInternalAsync(token);

                    _logger.LogDebug(
                        "Updated last accessed road for {StudentName} to {RoadId}",
                        token.StudentName,
                        roadId);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<AccessTokenCreationResult> RegenerateTokenAsync(Guid tokenId, string regeneratedBy)
        {
            await _lock.WaitAsync();
            try
            {
                var token = await LoadTokenByIdAsync(tokenId);
                if (token == null)
                {
                    return AccessTokenCreationResult.Failed("Token not found");
                }

                if (!token.IsActive)
                {
                    return AccessTokenCreationResult.Failed("Cannot regenerate a revoked token");
                }

                var oldTokenHash = token.TokenHash;

                // Generate new cryptographically secure token
                var tokenBytes = RandomNumberGenerator.GetBytes(TokenByteLength);
                var plaintextToken = Convert.ToBase64String(tokenBytes)
                    .Replace("+", "-")  // URL-safe
                    .Replace("/", "_")  // URL-safe
                    .TrimEnd('=');      // Remove padding

                var newTokenHash = HashToken(plaintextToken);

                // Update token with new hash
                token.TokenHash = newTokenHash;

                await SaveTokenInternalAsync(token);

                // Update index after successful save - remove old, add new
                RemoveFromIndex(oldTokenHash);
                AddToIndex(newTokenHash, token.TokenId);

                _logger.LogInformation(
                    "Regenerated access token for {StudentName} by {RegeneratedBy}",
                    token.StudentName,
                    regeneratedBy);

                return AccessTokenCreationResult.Succeeded(token, plaintextToken);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AccessToken>> GetTokensByEmailAsync(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return Array.Empty<AccessToken>();
            }

            var normalizedEmail = email.Trim().ToLowerInvariant();
            
            // Note: This still loads all tokens since email lookups span all tenants.
            // For high-volume scenarios, consider adding an email->tokenIds index.
            var tokens = await LoadAllTokensAsync();

            return tokens
                .Where(t => t.IsActive &&
                           string.Equals(t.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.StudentName)
                .ToList();
        }

        #region Private Methods

        private static string HashToken(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToBase64String(bytes);
        }

        private async Task SaveTokenAsync(AccessToken token)
        {
            await _lock.WaitAsync();
            try
            {
                await SaveTokenInternalAsync(token);

                // Update index - ConcurrentDictionary handles thread safety
                await EnsureIndexLoadedInternalAsync();
                _tokenHashIndex![token.TokenHash] = token.TokenId;
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task SaveTokenInternalAsync(AccessToken token)
        {
            var filePath = GetTokenFilePath(token.TenantId, token.TokenId);
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(token, options);
            await File.WriteAllTextAsync(filePath, json);
        }

        private async Task<AccessToken?> LoadTokenByIdAsync(Guid tokenId)
        {
            // Search across tenant directories for the token
            if (!Directory.Exists(_storagePath))
            {
                return null;
            }

            foreach (var tenantDir in Directory.GetDirectories(_storagePath))
            {
                var filePath = Path.Combine(tenantDir, "_tokens", $"{tokenId}.json");
                if (File.Exists(filePath))
                {
                    var json = await File.ReadAllTextAsync(filePath);
                    return JsonSerializer.Deserialize<AccessToken>(json);
                }
            }

            return null;
        }

        /// <summary>
        /// Loads all tokens for a specific tenant from disk.
        /// More efficient than LoadAllTokensAsync when you only need one tenant's tokens.
        /// </summary>
        private async Task<List<AccessToken>> LoadTokensForTenantAsync(Guid tenantId)
        {
            var tokens = new List<AccessToken>();
            var tokensDir = GetTenantTokensDirectory(tenantId);

            if (!Directory.Exists(tokensDir))
            {
                return tokens;
            }

            var tokenFiles = Directory.GetFiles(tokensDir, "*.json");
            foreach (var file in tokenFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var token = JsonSerializer.Deserialize<AccessToken>(json);
                    if (token != null)
                    {
                        tokens.Add(token);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load token from {File}", file);
                }
            }

            return tokens;
        }

        private async Task<List<AccessToken>> LoadAllTokensAsync()
        {
            var tokens = new List<AccessToken>();

            if (!Directory.Exists(_storagePath))
            {
                return tokens;
            }

            // Iterate through all tenant directories
            foreach (var tenantDir in Directory.GetDirectories(_storagePath))
            {
                var tokensDir = Path.Combine(tenantDir, "_tokens");
                if (!Directory.Exists(tokensDir))
                {
                    continue;
                }

                var tokenFiles = Directory.GetFiles(tokensDir, "*.json");
                foreach (var file in tokenFiles)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var token = JsonSerializer.Deserialize<AccessToken>(json);
                        if (token != null)
                        {
                            tokens.Add(token);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load token from {File}", file);
                    }
                }
            }

            return tokens;
        }

        /// <summary>
        /// Ensures the index is loaded. Must be called from within a lock.
        /// </summary>
        private async Task EnsureIndexLoadedAsync()
        {
            await EnsureIndexLoadedInternalAsync();
        }

        /// <summary>
        /// Internal implementation of index loading. Assumes caller holds the lock.
        /// </summary>
        private async Task EnsureIndexLoadedInternalAsync()
        {
            if (_indexLoaded && _tokenHashIndex != null)
            {
                return;
            }

            _tokenHashIndex = new ConcurrentDictionary<string, Guid>();
            var tokens = await LoadAllTokensAsync();

            foreach (var token in tokens.Where(t => t.IsActive))
            {
                _tokenHashIndex[token.TokenHash] = token.TokenId;
            }

            _indexLoaded = true;
            _logger.LogInformation("Loaded token index with {Count} active tokens", _tokenHashIndex.Count);
        }

        /// <summary>
        /// Removes a hash from the index. Thread-safe via ConcurrentDictionary.
        /// </summary>
        private void RemoveFromIndex(string tokenHash)
        {
            _tokenHashIndex?.TryRemove(tokenHash, out _);
        }

        /// <summary>
        /// Adds or updates a hash in the index. Thread-safe via ConcurrentDictionary.
        /// </summary>
        private void AddToIndex(string tokenHash, Guid tokenId)
        {
            if (_tokenHashIndex != null)
            {
                _tokenHashIndex[tokenHash] = tokenId;
            }
        }

        private string GetTokenFilePath(Guid tenantId, Guid tokenId)
        {
            // Store tokens inside tenant folder: {tenantId}/_tokens/{tokenId}.json
            return Path.Combine(_storagePath, tenantId.ToString(), "_tokens", $"{tokenId}.json");
        }

        private string GetTenantTokensDirectory(Guid tenantId)
        {
            return Path.Combine(_storagePath, tenantId.ToString(), "_tokens");
        }

        /// <summary>
        /// Validates email format using MailAddress parsing.
        /// </summary>
        /// <param name="email">The email address to validate.</param>
        /// <returns><c>true</c> if the email format is valid; otherwise <c>false</c>.</returns>
        private static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return false;
            }

            try
            {
                // Use MailAddress for validation - it handles edge cases well
                var addr = new System.Net.Mail.MailAddress(email.Trim());
                // Ensure the address matches what was parsed (catches some edge cases)
                return addr.Address.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>
        /// Creates a consistent, non-reversible hash of an email for logging purposes.
        /// The same email will always produce the same hash, allowing log correlation
        /// without exposing PII.
        /// </summary>
        /// <param name="email">The email address to hash.</param>
        /// <returns>A short hash string suitable for logging.</returns>
        private static string HashEmailForLogging(string? email)
        {
            if (string.IsNullOrEmpty(email))
            {
                return "[no-email]";
            }

            // Normalize email before hashing for consistency
            var normalized = email.Trim().ToLowerInvariant();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            
            // Use first 8 bytes (16 hex chars) - enough for correlation, not too long for logs
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }

        private static string MaskEmail(string? email)
        {
            if (string.IsNullOrEmpty(email)) return "[empty]";
            var atIndex = email.IndexOf('@');
            if (atIndex <= 0) return "[invalid]";
            return $"{email[0]}***@{email[atIndex + 1]}***";
        }

        #endregion
    }
}
