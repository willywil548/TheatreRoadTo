using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Provides tenant-aware SendGrid inbound email processing and retrieval operations.
    /// </summary>
    public interface ISendGridEmailService
    {
        /// <summary>
        /// Validates, routes, and conditionally persists an inbound SendGrid email.
        /// </summary>
        /// <param name="context">The current HTTP context for the inbound request.</param>
        /// <param name="email">The parsed inbound SendGrid email payload.</param>
        /// <returns>The processing outcome with validation and persistence details.</returns>
        Task<InboundEmailProcessResult> ProcessInboundEmailAsync(HttpContext context, SendGridInboundEmail email);

        /// <summary>
        /// Retrieves and decrypts a previously stored email by filename.
        /// </summary>
        /// <param name="filename">The tenant-qualified or legacy email filename.</param>
        /// <returns>The lookup result containing status and optional email payload.</returns>
        Task<StoredEmailLookupResult> GetStoredEmailAsync(string filename);

        /// <summary>
        /// Lists all stored tenant email artifacts.
        /// </summary>
        /// <returns>A collection of stored email summaries.</returns>
        Task<IReadOnlyList<StoredEmailSummary>> ListStoredEmailsAsync();

        /// <summary>
        /// Gets the current health and configuration status for SendGrid email processing.
        /// </summary>
        /// <returns>A health status snapshot.</returns>
        SendGridHealthStatus GetHealthStatus();
    }

    /// <summary>
    /// Represents the outcome of inbound email processing.
    /// </summary>
    public sealed record InboundEmailProcessResult(
        bool IsValid,
        string? ValidationReason,
        string? Filename,
        string? From,
        string? Subject,
        double SpamScore,
        bool Saved = true);

    /// <summary>
    /// Represents the status of a stored-email lookup request.
    /// </summary>
    public enum StoredEmailLookupStatus
    {
        /// <summary>
        /// The lookup succeeded.
        /// </summary>
        Success,

        /// <summary>
        /// The provided filename format is invalid.
        /// </summary>
        InvalidFilename,

        /// <summary>
        /// The resolved path is invalid.
        /// </summary>
        InvalidPath,

        /// <summary>
        /// The requested email was not found.
        /// </summary>
        NotFound
    }

    /// <summary>
    /// Represents the result of looking up a stored email.
    /// </summary>
    /// <param name="Status">The lookup status.</param>
    /// <param name="Email">The decrypted email when lookup succeeds.</param>
    public sealed record StoredEmailLookupResult(StoredEmailLookupStatus Status, SendGridInboundEmail? Email = null);

    /// <summary>
    /// Represents a stored email file summary.
    /// </summary>
    /// <param name="Filename">The tenant-qualified filename.</param>
    /// <param name="Size">The file size in bytes.</param>
    /// <param name="Created">The file creation timestamp in UTC.</param>
    public sealed record StoredEmailSummary(string Filename, long Size, DateTime Created);

    /// <summary>
    /// Represents webhook validation requirements in health output.
    /// </summary>
    /// <param name="IpRequired">Whether IP-based validation is required.</param>
    /// <param name="AuthRequired">Whether auth signature validation is required.</param>
    public sealed record SendGridHealthValidationStatus(bool IpRequired, bool AuthRequired);

    /// <summary>
    /// Represents SendGrid email service health output.
    /// </summary>
    /// <param name="Status">The overall health status value.</param>
    /// <param name="StoragePath">A storage path hint for health visibility.</param>
    /// <param name="Encryption">Encryption state value.</param>
    /// <param name="Validation">Validation requirement details.</param>
    /// <param name="Timestamp">The UTC timestamp for the health snapshot.</param>
    public sealed record SendGridHealthStatus(
        string Status,
        string StoragePath,
        string Encryption,
        SendGridHealthValidationStatus Validation,
        DateTime Timestamp);

    /// <summary>
    /// Represents tenant routing resolution states for inbound recipients.
    /// </summary>
    internal enum TenantRoutingStatus
    {
        /// <summary>
        /// A known tenant was matched.
        /// </summary>
        Matched,

        /// <summary>
        /// A known tenant and road pair was matched.
        /// </summary>
        MatchedTenantRoad,

        /// <summary>
        /// The recipient block could not be read as a routable address.
        /// </summary>
        Unreadable,

        /// <summary>
        /// Recipient values were readable but did not map to a known tenant.
        /// </summary>
        NoKnownTenant
    }

    /// <summary>
    /// Processes inbound SendGrid emails using tenant-aware routing and encrypted file storage.
    /// </summary>
    public partial class SendGridEmailService : ISendGridEmailService
    {
        [GeneratedRegex(@"[\r\n\t\x00-\x1F\x7F]", RegexOptions.Compiled)]
        private static partial Regex LogSanitizationRegex();

        private readonly ILogger<SendGridEmailService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IEmailEncryptionService _encryptionService;
        private readonly ISendGridWebhookValidator _webhookValidator;
        private readonly ITenantManagerService _tenantManagerService;
        private readonly Guid? _demoTenantId;

        /// <summary>
        /// Initializes a new instance of the <see cref="SendGridEmailService"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="encryptionService">The email encryption service.</param>
        /// <param name="webhookValidator">The webhook validator service.</param>
        /// <param name="tenantManagerService">The tenant manager service.</param>
        public SendGridEmailService(
            ILogger<SendGridEmailService> logger,
            IConfiguration configuration,
            IEmailEncryptionService encryptionService,
            ISendGridWebhookValidator webhookValidator,
            ITenantManagerService tenantManagerService)
        {
            _logger = logger;
            _configuration = configuration;
            _encryptionService = encryptionService;
            _webhookValidator = webhookValidator;
            _tenantManagerService = tenantManagerService;

            var demoTenantId = _configuration.GetValue<string>("TenantManager:DemoTenantId")
                ?? TenantManagerService.DefaultDemoGuid;

            _demoTenantId = Guid.TryParse(demoTenantId, out var parsedDemoTenantId)
                ? parsedDemoTenantId
                : null;
        }

        /// <inheritdoc />
        public async Task<InboundEmailProcessResult> ProcessInboundEmailAsync(HttpContext context, SendGridInboundEmail email)
        {
            _logger.LogInformation("Received inbound email request from SendGrid");

            // First gate: validate webhook source before doing any processing work.
            var validationResult = await _webhookValidator.ValidateRequestAsync(context);
            if (!validationResult.IsValid)
            {
                _logger.LogWarning("Webhook validation failed: {Reason}", SanitizeForLog(validationResult.Reason));
                return new InboundEmailProcessResult(
                    IsValid: false,
                    ValidationReason: validationResult.Reason,
                    Filename: null,
                    From: email.GetFromEmail(),
                    Subject: email.Subject,
                    SpamScore: email.GetSpamScoreValue());
            }

            _logger.LogInformation("Webhook validation passed. Headers captured: {HasHeaders}",
                validationResult.Headers != null);

            // Second gate: resolve tenant (or tenant.road) from recipient local-part.
            var tenantResolution = ResolveTenantIdFromRecipients(email);
            if (tenantResolution.Status == TenantRoutingStatus.Unreadable)
            {
                // Acknowledge but do not save when routing data cannot be interpreted.
                _logger.LogWarning("Inbound email acknowledged but not saved: unreadable recipient block. To: {To}",
                    SanitizeForLog(email.To));

                return new InboundEmailProcessResult(
                    IsValid: true,
                    ValidationReason: "Unreadable recipient block",
                    Filename: null,
                    From: email.GetFromEmail(),
                    Subject: email.Subject,
                    SpamScore: email.GetSpamScoreValue(),
                    Saved: false);
            }

            if ((tenantResolution.Status != TenantRoutingStatus.Matched &&
                 tenantResolution.Status != TenantRoutingStatus.MatchedTenantRoad) ||
                 !tenantResolution.TenantId.HasValue)
            {
                // Acknowledge but do not save when no known tenant can be mapped.
                _logger.LogWarning("Inbound email acknowledged but not saved: no matching tenant ID found in recipient fields. To: {To}",
                    SanitizeForLog(email.To));

                return new InboundEmailProcessResult(
                    IsValid: true,
                    ValidationReason: "No matching tenant found in recipient block",
                    Filename: null,
                    From: email.GetFromEmail(),
                    Subject: email.Subject,
                    SpamScore: email.GetSpamScoreValue(),
                    Saved: false);
            }

            var tenantId = tenantResolution.TenantId.Value;

            // Explicitly exclude demo tenant from persistence workflows.
            if (_demoTenantId.HasValue && tenantId == _demoTenantId.Value)
            {
                _logger.LogInformation("Inbound email acknowledged but not saved: demo tenant is excluded. TenantId: {TenantId}", tenantId);

                return new InboundEmailProcessResult(
                    IsValid: true,
                    ValidationReason: "Demo tenant is excluded from inbound email processing",
                    Filename: null,
                    From: email.GetFromEmail(),
                    Subject: email.Subject,
                    SpamScore: email.GetSpamScoreValue(),
                    Saved: false);
            }

            // Enrich inbound payload with system-managed metadata.
            email.ReceivedAt = DateTime.UtcNow.ToString("O");
            email.Encrypted = true;
            email.ValidatedSource = validationResult.IsValid;
            email.WebhookHeaders = validationResult.Headers;

            // Capture attachment metadata for audit/inspection; file bytes are not persisted here.
            var form = await context.Request.ReadFormAsync();
            if (form.Files.Count > 0)
            {
                email.Attachments = form.Files.Select(file => new EmailAttachment
                {
                    Filename = file.FileName,
                    ContentType = file.ContentType,
                    Length = file.Length
                }).ToList();
            }

            // Log with masking/sanitization to reduce PII and log-injection risk.
            _logger.LogInformation("Parsed email - From: {From}, To: {To}, Subject: {Subject}",
                MaskEmail(email.GetFromEmail()),
                MaskEmail(email.GetToEmail()),
                SanitizeForLog(email.Subject));
            _logger.LogInformation("Email validation - DKIM: {Dkim}, SPF: {Spf}, Spam Score: {SpamScore}",
                email.IsDkimValid(), email.IsSpfValid(), email.GetSpamScoreValue());

            if (email.IsLikelySpam())
            {
                // Current behavior keeps spam for analysis; downstream readers can filter.
                _logger.LogWarning("Email flagged as spam (score: {SpamScore}), saving but marking as spam",
                    email.GetSpamScoreValue());
            }

            // Tenant-scoped storage keeps lifecycle aligned to tenant deletion.
            string tenantEmailStoragePath = GetTenantEmailStoragePath(tenantId);
            if (!Directory.Exists(tenantEmailStoragePath))
            {
                Directory.CreateDirectory(tenantEmailStoragePath);
            }

            // Compose storage filename with timestamp + sender hint + random suffix.
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string fromEmail = email.GetFromEmail()?.Replace("@", "_at_") ?? "unknown";
            string sanitizedFrom = string.Join("_", fromEmail.Split(Path.GetInvalidFileNameChars()));
            string filename = $"email_{timestamp}_{sanitizedFrom}_{Guid.NewGuid()}.enc";
            string filePath = Path.Combine(tenantEmailStoragePath, filename);

            // Persist tenant-qualified relative file identity for retrieval endpoints.
            email.StoredAs = $"{tenantId:D}/{filename}";

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // Encrypt and save payload to disk.
            string jsonContent = JsonSerializer.Serialize(email, options);
            await _encryptionService.WriteEncryptedFileAsync(filePath, jsonContent);

            // If routed as tenant.road, also materialize an address on that road.
            if (tenantResolution.Status == TenantRoutingStatus.MatchedTenantRoad && tenantResolution.RoadId.HasValue)
            {
                CreateRoadAddressFromEmail(tenantId, tenantResolution.RoadId.Value, email);
            }

            _logger.LogInformation("Encrypted email saved successfully for tenant {TenantId}", tenantId);

            return new InboundEmailProcessResult(
                IsValid: true,
                ValidationReason: null,
                Filename: email.StoredAs,
                From: email.GetFromEmail(),
                Subject: email.Subject,
                SpamScore: email.GetSpamScoreValue(),
                Saved: true);
        }

        /// <inheritdoc />
        public async Task<StoredEmailLookupResult> GetStoredEmailAsync(string filename)
        {
            var resolvedPath = ResolveStoredEmailPath(filename);
            if (resolvedPath.Status != StoredEmailLookupStatus.Success || string.IsNullOrEmpty(resolvedPath.Path))
            {
                return new StoredEmailLookupResult(resolvedPath.Status);
            }

            var decryptedContent = await _encryptionService.ReadEncryptedFileAsync(resolvedPath.Path);
            var email = JsonSerializer.Deserialize<SendGridInboundEmail>(decryptedContent);

            _logger.LogInformation("Retrieved and decrypted email successfully");
            return new StoredEmailLookupResult(StoredEmailLookupStatus.Success, email);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<StoredEmailSummary>> ListStoredEmailsAsync()
        {
            var files = new List<StoredEmailSummary>();

            foreach (var tenant in _tenantManagerService.GetTenants())
            {
                string tenantFolder = GetTenantEmailStoragePath(tenant.TenantId);
                if (!Directory.Exists(tenantFolder))
                {
                    continue;
                }

                var tenantFiles = Directory
                    .GetFiles(tenantFolder, "email_*.enc")
                    .Select(path =>
                    {
                        var fileInfo = new FileInfo(path);
                        return new StoredEmailSummary(
                            Filename: $"{tenant.TenantId:D}/{Path.GetFileName(path)}",
                            Size: fileInfo.Length,
                            Created: File.GetCreationTimeUtc(path));
                    });

                files.AddRange(tenantFiles);
            }

            IReadOnlyList<StoredEmailSummary> ordered = files
                .OrderByDescending(file => file.Created)
                .ToList();

            return Task.FromResult(ordered);
        }

        /// <inheritdoc />
        public SendGridHealthStatus GetHealthStatus()
        {
            bool encryptionEnabled = _configuration.GetValue<bool>("SendGrid:EnableEncryption", true);
            bool requireIpValidation = _configuration.GetValue<bool>("SendGrid:RequireIpValidation", false);
            bool requireAuthValidation = _configuration.GetValue<bool>("SendGrid:RequireAuthValidation", false);

            return new SendGridHealthStatus(
                Status: "healthy",
                StoragePath: "{tenantId}/emails",
                Encryption: encryptionEnabled ? "enabled" : "disabled",
                Validation: new SendGridHealthValidationStatus(requireIpValidation, requireAuthValidation),
                Timestamp: DateTime.UtcNow);
        }

        /// <summary>
        /// Resolves a tenant-qualified filename into a secure full path.
        /// </summary>
        /// <param name="filename">The input filename value.</param>
        /// <returns>The resolution status and resolved full path when successful.</returns>
        private (StoredEmailLookupStatus Status, string? Path) ResolveStoredEmailPath(string filename)
        {
            // Normalize separators to support URL and file-style inputs consistently.
            var normalized = filename.Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return (StoredEmailLookupStatus.InvalidFilename, null);
            }

            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Preferred format is {tenantId}/{emailFile}.
            if (parts.Length == 2)
            {
                if (!Guid.TryParse(parts[0], out var tenantId))
                {
                    return (StoredEmailLookupStatus.InvalidFilename, null);
                }

                if (!IsValidEmailFilename(parts[1]))
                {
                    return (StoredEmailLookupStatus.InvalidFilename, null);
                }

                var tenantRoot = GetTenantEmailStoragePath(tenantId);
                var tenantFilePath = Path.Combine(tenantRoot, Path.GetFileName(parts[1]));
                var fullPath = Path.GetFullPath(tenantFilePath);
                var allowedPath = Path.GetFullPath(tenantRoot);

                // Defense-in-depth path validation against traversal.
                if (!fullPath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Path traversal attempt detected");
                    return (StoredEmailLookupStatus.InvalidPath, null);
                }

                if (!File.Exists(fullPath))
                {
                    return (StoredEmailLookupStatus.NotFound, null);
                }

                return (StoredEmailLookupStatus.Success, fullPath);
            }

            // Backward-compatible lookup by bare filename across tenant folders.
            string safeFilename = Path.GetFileName(normalized);
            if (!IsValidEmailFilename(safeFilename))
            {
                return (StoredEmailLookupStatus.InvalidFilename, null);
            }

            foreach (var tenant in _tenantManagerService.GetTenants())
            {
                var tenantPath = Path.Combine(GetTenantEmailStoragePath(tenant.TenantId), safeFilename);
                if (File.Exists(tenantPath))
                {
                    return (StoredEmailLookupStatus.Success, tenantPath);
                }
            }

            return (StoredEmailLookupStatus.NotFound, null);
        }

        /// <summary>
        /// Resolves a known tenant ID from recipient values.
        /// </summary>
        /// <param name="email">The inbound email payload.</param>
        /// <returns>The routing status and resolved tenant ID when matched.</returns>
        private (TenantRoutingStatus Status, Guid? TenantId, Guid? RoadId) ResolveTenantIdFromRecipients(SendGridInboundEmail email)
        {
            bool foundRecipientToken = false;
            bool foundReadableAddress = false;

            foreach (var recipient in EnumerateRecipients(email))
            {
                foundRecipientToken = true;

                foreach (var localPart in EnumerateLocalParts(recipient))
                {
                    foundReadableAddress = true;

                    // tenant.road format (dot delimiter) routes directly to a road.
                    var routeParts = localPart.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (routeParts.Length == 2 &&
                        Guid.TryParse(routeParts[0], out var tenantIdFromPair) &&
                        Guid.TryParse(routeParts[1], out var roadIdFromPair))
                    {
                        var tenant = _tenantManagerService.GetTenant(tenantIdFromPair);
                        bool roadBelongsToTenant = tenant?.Roads.Any(road => road.RoadId == roadIdFromPair) ?? false;
                        if (roadBelongsToTenant)
                        {
                            return (TenantRoutingStatus.MatchedTenantRoad, tenantIdFromPair, roadIdFromPair);
                        }
                    }

                    // tenant-only format routes to tenant-level processing.
                    if (Guid.TryParse(localPart, out var tenantId) && _tenantManagerService.GetTenant(tenantId) != null)
                    {
                        return (TenantRoutingStatus.Matched, tenantId, null);
                    }
                }
            }

            if (!foundRecipientToken || !foundReadableAddress)
            {
                return (TenantRoutingStatus.Unreadable, null, null);
            }

            return (TenantRoutingStatus.NoKnownTenant, null, null);
        }

        /// <summary>
        /// Creates a notification address on a road using inbound email content.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <param name="roadId">The road ID.</param>
        /// <param name="email">The inbound email payload.</param>
        private void CreateRoadAddressFromEmail(Guid tenantId, Guid roadId, SendGridInboundEmail email)
        {
            try
            {
                var road = _tenantManagerService.GetRoad(roadId);
                if (road.TenantId != tenantId)
                {
                    _logger.LogWarning("Inbound email route mismatch: road {RoadId} does not belong to tenant {TenantId}", roadId, tenantId);
                    return;
                }

                // Create a simple notification address from email metadata/body.
                var address = new Address
                {
                    Location = DateTime.UtcNow,
                    Title = string.IsNullOrWhiteSpace(email.Subject) ? "Inbound Email" : email.Subject,
                    Description = $"Inbound email from {email.GetFromDisplayName() ?? email.GetFromEmail() ?? "unknown sender"}",
                    Content = email.GetBodyContent() ?? email.GetTextBody() ?? email.GetHtmlBody() ?? string.Empty,
                    AddressType = AddressType.Notification,
                    DelayRelease = false
                };

                // Append and persist by re-saving the road.
                var existingAddresses = road.Addresses ?? Array.Empty<Address>();
                road.Addresses = [.. existingAddresses, address];
                _tenantManagerService.SaveRoad(road);

                _logger.LogInformation("Created inbound email address for tenant {TenantId}, road {RoadId}", tenantId, roadId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create road address from inbound email for tenant {TenantId}, road {RoadId}", tenantId, roadId);
            }
        }

        /// <summary>
        /// Enumerates recipient strings from inbound fields and parsed envelope values.
        /// </summary>
        /// <param name="email">The inbound email payload.</param>
        /// <returns>Recipient string values for routing inspection.</returns>
        private static IEnumerable<string> EnumerateRecipients(SendGridInboundEmail email)
        {
            if (!string.IsNullOrWhiteSpace(email.To))
            {
                yield return email.To;
            }

            var envelope = email.GetEnvelope();
            if (envelope?.To != null)
            {
                foreach (var recipient in envelope.To.Where(static r => !string.IsNullOrWhiteSpace(r)))
                {
                    yield return recipient;
                }
            }
        }

        /// <summary>
        /// Enumerates email local-part values from a recipient input string.
        /// </summary>
        /// <param name="input">The recipient input string.</param>
        /// <returns>Extracted local-part values.</returns>
        private static IEnumerable<string> EnumerateLocalParts(string input)
        {
            // Multiple recipients may be comma/semicolon separated.
            foreach (var token in input.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Remove common wrapper characters from mailbox token.
                var cleanToken = token.Trim().Trim('"', '\'', '<', '>');
                if (string.IsNullOrWhiteSpace(cleanToken))
                {
                    continue;
                }

                // Local-part is everything before '@'.
                int atIndex = cleanToken.IndexOf('@');
                if (atIndex <= 0)
                {
                    continue;
                }

                var localPart = cleanToken[..atIndex].Trim('"', '\'', '<', '>');
                if (!string.IsNullOrWhiteSpace(localPart))
                {
                    yield return localPart;
                }
            }
        }

        /// <summary>
        /// Gets the tenant-specific email storage path.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <returns>The tenant email storage folder path.</returns>
        private string GetTenantEmailStoragePath(Guid tenantId)
        {
            return Path.Combine(_tenantManagerService.GetTenantRootPath(tenantId), "emails");
        }

        private static bool IsValidEmailFilename(string filename)
        {
            // Enforce expected storage artifact naming to reduce ambiguous lookups.
            return !string.IsNullOrEmpty(filename) &&
                   filename.StartsWith("email_", StringComparison.Ordinal) &&
                   filename.EndsWith(".enc", StringComparison.Ordinal);
        }

        private static string SanitizeForLog(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            // Remove control characters that could forge/poison logs.
            var sanitized = LogSanitizationRegex().Replace(input, " ");
            const int maxLogLength = 200;
            if (sanitized.Length > maxLogLength)
            {
                // Bound log payload size for safety and readability.
                sanitized = string.Concat(sanitized.AsSpan(0, maxLogLength), "...");
            }

            return sanitized;
        }

        private static string MaskEmail(string? email)
        {
            if (string.IsNullOrEmpty(email))
            {
                return "[empty]";
            }

            var atIndex = email.IndexOf('@');
            if (atIndex <= 0)
            {
                return "[invalid]";
            }

            var localPart = email[..atIndex];
            var domainPart = email[(atIndex + 1)..];

            // Keep minimal signal for diagnostics while masking PII.
            var maskedLocal = localPart.Length > 0
                ? $"{localPart[0]}***"
                : "***";

            var lastDotIndex = domainPart.LastIndexOf('.');
            string maskedDomain;
            if (lastDotIndex > 0)
            {
                var tld = domainPart[lastDotIndex..];
                maskedDomain = $"{domainPart[0]}***{tld}";
            }
            else
            {
                maskedDomain = $"{domainPart[0]}***";
            }

            return $"{maskedLocal}@{maskedDomain}";
        }
    }
}
