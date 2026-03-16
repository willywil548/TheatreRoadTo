using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Event args for a successfully ingested inbound email.
    /// </summary>
    /// <param name="TenantId">Tenant that received the email.</param>
    public sealed class EmailIngestedEventArgs(Guid tenantId) : EventArgs
    {
        public Guid TenantId { get; } = tenantId;
    }

    /// <summary>
    /// Represents the result of attempting to get a stored email for a caller.
    /// </summary>
    /// <param name="Allowed">Whether the caller is authorized to read the requested email.</param>
    /// <param name="EmailResult">The lookup result for the requested email.</param>
    public sealed record EmailGetAccessResult(bool Allowed, StoredEmailLookupResult EmailResult);

    /// <summary>
    /// Represents the result of attempting to list stored emails for a caller.
    /// </summary>
    /// <param name="Allowed">Whether the caller is authorized to list emails.</param>
    /// <param name="Emails">The visible emails for the caller and optional filter.</param>
    public sealed record EmailListAccessResult(bool Allowed, IReadOnlyList<StoredEmailSummary> Emails);

    /// <summary>
    /// Represents the result of attempting to list processing statuses for a caller.
    /// </summary>
    /// <param name="Allowed">Whether the caller is authorized to list processing statuses.</param>
    /// <param name="Statuses">The visible processing statuses for the caller and optional filter.</param>
    public sealed record EmailProcessingListAccessResult(bool Allowed, IReadOnlyList<EmailProcessingStatusSummary> Statuses);

    /// <summary>
    /// Represents the result of attempting to get processing artifact details for a caller.
    /// </summary>
    /// <param name="Allowed">Whether the caller is authorized to access processing details.</param>
    /// <param name="Found">Whether the requested processing artifact exists.</param>
    /// <param name="Details">The processing details payload when found and allowed.</param>
    public sealed record EmailProcessingDetailsAccessResult(bool Allowed, bool Found, EmailProcessingDetails? Details);

    /// <summary>
    /// Represents caller visibility for SendGrid email operations.
    /// </summary>
    /// <param name="Allowed">Whether the caller has any email access.</param>
    /// <param name="HasGlobalAccess">Whether the caller can access all tenants.</param>
    /// <param name="AllowedTenantIds">Tenant IDs visible to the caller.</param>
    public sealed record EmailAccessScope(bool Allowed, bool HasGlobalAccess, HashSet<Guid> AllowedTenantIds);

    /// <summary>
    /// Represents a summary view of asynchronous email processing state.
    /// </summary>
    /// <param name="TenantId">The tenant that owns the processing artifact.</param>
    /// <param name="ProcessingId">The processing identifier for the inbound email.</param>
    /// <param name="Status">The current processing state value.</param>
    /// <param name="Attempt">The current attempt count.</param>
    /// <param name="CreatedCount">The number of created addresses for this processing run.</param>
    /// <param name="DroppedCount">The number of dropped extraction items.</param>
    /// <param name="UpdatedAt">The latest processing update timestamp, when available.</param>
    public sealed record EmailProcessingStatusSummary(
        Guid TenantId,
        Guid ProcessingId,
        string Status,
        int Attempt,
        int CreatedCount,
        int DroppedCount,
        DateTime? UpdatedAt);

    /// <summary>
    /// Represents detailed artifact JSON for one processing run.
    /// </summary>
    /// <param name="StateJson">The raw processing state JSON payload.</param>
    /// <param name="CreatedJson">The created output items JSON payload.</param>
    /// <param name="DroppedJson">The dropped output items JSON payload.</param>
    public sealed record EmailProcessingDetails(string StateJson, string CreatedJson, string DroppedJson);

    /// <summary>
    /// Provides tenant-aware SendGrid inbound email processing and retrieval operations.
    /// </summary>
    public interface ISendGridEmailService
    {
        /// <summary>
        /// Raised when an inbound email is successfully ingested and persisted.
        /// </summary>
        event EventHandler<EmailIngestedEventArgs>? InboundEmailIngested;

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
        /// Attempts to retrieve a stored email visible to the provided caller.
        /// </summary>
        /// <param name="user">The caller claims principal.</param>
        /// <param name="filename">The requested filename.</param>
        /// <returns>An access result containing authorization state and email lookup result.</returns>
        Task<EmailGetAccessResult> TryGetStoredEmailForAccessAsync(ClaimsPrincipal user, string filename);

        /// <summary>
        /// Lists all stored tenant email artifacts.
        /// </summary>
        /// <returns>A collection of stored email summaries.</returns>
        Task<IReadOnlyList<StoredEmailSummary>> ListStoredEmailsAsync();

        /// <summary>
        /// Lists stored email artifacts visible to a caller based on access scope.
        /// </summary>
        /// <param name="tenantIdFilter">Optional tenant filter. If provided, only that tenant is returned.</param>
        /// <param name="hasGlobalAccess">True when caller can access all tenants.</param>
        /// <param name="allowedTenantIds">Tenant IDs caller can access when not global.</param>
        /// <returns>A collection of email summaries constrained to caller visibility.</returns>
        Task<IReadOnlyList<StoredEmailSummary>> ListStoredEmailsForAccessAsync(
            Guid? tenantIdFilter,
            bool hasGlobalAccess,
            IReadOnlyCollection<Guid> allowedTenantIds);

        /// <summary>
        /// Attempts to list stored emails visible to the provided caller.
        /// </summary>
        /// <param name="user">The caller claims principal.</param>
        /// <param name="tenantIdFilter">Optional tenant filter. If provided, only that tenant is returned.</param>
        /// <returns>An access result containing authorization state and visible emails when allowed.</returns>
        Task<EmailListAccessResult> TryListStoredEmailsForAccessAsync(ClaimsPrincipal user, Guid? tenantIdFilter);

        /// <summary>
        /// Attempts to list processing statuses visible to the provided caller.
        /// </summary>
        /// <param name="user">The caller claims principal.</param>
        /// <param name="tenantIdFilter">Optional tenant filter. If provided, only that tenant is returned.</param>
        /// <returns>An access result containing authorization state and visible processing statuses when allowed.</returns>
        Task<EmailProcessingListAccessResult> TryListProcessingStatusesForAccessAsync(ClaimsPrincipal user, Guid? tenantIdFilter);

        /// <summary>
        /// Attempts to get processing artifact details visible to the provided caller.
        /// </summary>
        /// <param name="user">The caller claims principal.</param>
        /// <param name="tenantId">The tenant that owns the processing artifact.</param>
        /// <param name="processingId">The processing artifact identifier.</param>
        /// <returns>An access result containing authorization state and processing details when available.</returns>
        Task<EmailProcessingDetailsAccessResult> TryGetProcessingDetailsForAccessAsync(
            ClaimsPrincipal user,
            Guid tenantId,
            Guid processingId);

        /// <summary>
        /// Gets the current health and configuration status for SendGrid email processing.
        /// </summary>
        /// <returns>A health status snapshot.</returns>
        SendGridHealthStatus GetHealthStatus();

        /// <summary>
        /// Resolves the caller's email access scope from identity and group memberships.
        /// </summary>
        /// <param name="user">The caller claims principal.</param>
        /// <returns>The access scope for email list/read operations.</returns>
        Task<EmailAccessScope> ResolveEmailAccessAsync(ClaimsPrincipal user);

        /// <summary>
        /// Determines whether a filename is accessible for the caller scope.
        /// </summary>
        /// <param name="filename">The requested filename.</param>
        /// <param name="accessScope">The caller access scope.</param>
        /// <returns><see langword="true"/> when the filename is in scope; otherwise <see langword="false"/>.</returns>
        bool CanAccessFilename(string filename, EmailAccessScope accessScope);
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
        bool Saved = true,
        Guid? ProcessingId = null,
        bool QueuedForProcessing = false);

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
        public event EventHandler<EmailIngestedEventArgs>? InboundEmailIngested;

        [GeneratedRegex(@"[\r\n\t\x00-\x1F\x7F]", RegexOptions.Compiled)]
        private static partial Regex LogSanitizationRegex();

        private readonly ILogger<SendGridEmailService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IEmailEncryptionService _encryptionService;
        private readonly ISendGridWebhookValidator _webhookValidator;
        private readonly IInboundEmailProcessingQueue _processingQueue;
        private readonly ISecurityGroupService _securityGroupService;
        private readonly ITenantManagerService _tenantManagerService;
        private readonly Guid? _demoTenantId;

        /// <summary>
        /// Initializes a new instance of the <see cref="SendGridEmailService"/> class.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="encryptionService">The email encryption service.</param>
        /// <param name="webhookValidator">The webhook validator service.</param>
        /// <param name="processingQueue">The background processing queue.</param>
        /// <param name="securityGroupService">The security group service.</param>
        /// <param name="tenantManagerService">The tenant manager service.</param>
        public SendGridEmailService(
            ILogger<SendGridEmailService> logger,
            IConfiguration configuration,
            IEmailEncryptionService encryptionService,
            ISendGridWebhookValidator webhookValidator,
            IInboundEmailProcessingQueue processingQueue,
            ISecurityGroupService securityGroupService,
            ITenantManagerService tenantManagerService)
        {
            _logger = logger;
            _configuration = configuration;
            _encryptionService = encryptionService;
            _webhookValidator = webhookValidator;
            _processingQueue = processingQueue;
            _securityGroupService = securityGroupService;
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
                    SpamScore: email.GetSpamScoreValue(),
                    Saved: false);
            }

            _logger.LogInformation("Webhook validation passed. Headers captured: {HasHeaders}",
                validationResult.Headers != null);

            // Require a valid sender email identity; acknowledge but do not ingest if missing.
            var fromEmail = email.GetFromEmail();
            if (string.IsNullOrWhiteSpace(fromEmail))
            {
                _logger.LogWarning("Inbound email acknowledged but not saved: missing sender email in From field.");
                return new InboundEmailProcessResult(
                    IsValid: true,
                    ValidationReason: "Missing sender email",
                    Filename: null,
                    From: null,
                    Subject: email.Subject,
                    SpamScore: email.GetSpamScoreValue(),
                    Saved: false);
            }

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
            string sanitizedSender = fromEmail.Replace("@", "_at_");
            string sanitizedFrom = string.Join("_", sanitizedSender.Split(Path.GetInvalidFileNameChars()));
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

            var processingId = Guid.NewGuid();

            // Create a deterministic per-email artifact workspace so queue workers can track
            // status, created outputs, and dropped items without re-reading controller context.
            await InitializeProcessingArtifactAsync(tenantId, processingId, email.StoredAs, tenantResolution, context.RequestAborted);

            // Preserve existing routing behavior: only subdomain-routed messages create timeline
            // addresses. Non-subdomain emails are still ingested and stored for audit/review.
            var shouldQueueForProcessing = tenantResolution.HasSubdomain &&
                                           (tenantResolution.Status == TenantRoutingStatus.Matched ||
                                            (tenantResolution.Status == TenantRoutingStatus.MatchedTenantRoad && tenantResolution.RoadId.HasValue));

            if (shouldQueueForProcessing)
            {
                // Route either to one road (tenant.road local-part) or all tenant roads.
                var routingMode = tenantResolution.Status == TenantRoutingStatus.MatchedTenantRoad
                    ? InboundEmailRoutingMode.Road
                    : InboundEmailRoutingMode.Tenant;

                await _processingQueue.QueueAsync(new InboundEmailProcessingRequest(
                    TenantId: tenantId,
                    EmailId: processingId,
                    StoredAs: email.StoredAs!,
                    RoutingMode: routingMode,
                    RoadId: tenantResolution.RoadId,
                    HasSubdomain: tenantResolution.HasSubdomain), context.RequestAborted);
            }

            _logger.LogInformation("Encrypted email saved successfully for tenant {TenantId}", tenantId);

            InboundEmailIngested?.Invoke(this, new EmailIngestedEventArgs(tenantId));

            return new InboundEmailProcessResult(
                IsValid: true,
                ValidationReason: null,
                Filename: email.StoredAs,
                From: fromEmail,
                Subject: email.Subject,
                SpamScore: email.GetSpamScoreValue(),
                Saved: true,
                ProcessingId: processingId,
                QueuedForProcessing: shouldQueueForProcessing);
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
        public async Task<EmailGetAccessResult> TryGetStoredEmailForAccessAsync(ClaimsPrincipal user, string filename)
        {
            var access = await ResolveEmailAccessAsync(user);
            if (!access.Allowed || !CanAccessFilename(filename, access))
            {
                return new EmailGetAccessResult(false, new StoredEmailLookupResult(StoredEmailLookupStatus.NotFound));
            }

            var emailResult = await GetStoredEmailAsync(filename);
            return new EmailGetAccessResult(true, emailResult);
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
        public async Task<IReadOnlyList<StoredEmailSummary>> ListStoredEmailsForAccessAsync(
            Guid? tenantIdFilter,
            bool hasGlobalAccess,
            IReadOnlyCollection<Guid> allowedTenantIds)
        {
            var allFiles = await ListStoredEmailsAsync();

            IReadOnlyList<StoredEmailSummary> visibleFiles = allFiles
                .Where(file =>
                    TryExtractTenantIdFromFilename(file.Filename, out var fileTenantId) &&
                    (hasGlobalAccess || allowedTenantIds.Contains(fileTenantId)) &&
                    (!tenantIdFilter.HasValue || fileTenantId == tenantIdFilter.Value))
                .ToList();

            return visibleFiles;
        }

        /// <inheritdoc />
        public async Task<EmailListAccessResult> TryListStoredEmailsForAccessAsync(ClaimsPrincipal user, Guid? tenantIdFilter)
        {
            var access = await ResolveEmailAccessAsync(user);
            if (!access.Allowed)
            {
                return new EmailListAccessResult(false, []);
            }

            if (tenantIdFilter.HasValue && !access.HasGlobalAccess && !access.AllowedTenantIds.Contains(tenantIdFilter.Value))
            {
                return new EmailListAccessResult(false, []);
            }

            var visibleFiles = await ListStoredEmailsForAccessAsync(
                tenantIdFilter,
                access.HasGlobalAccess,
                access.AllowedTenantIds);

            return new EmailListAccessResult(true, visibleFiles);
        }

        /// <inheritdoc />
        public async Task<EmailProcessingListAccessResult> TryListProcessingStatusesForAccessAsync(ClaimsPrincipal user, Guid? tenantIdFilter)
        {
            var access = await ResolveEmailAccessAsync(user);
            if (!access.Allowed)
            {
                return new EmailProcessingListAccessResult(false, []);
            }

            if (tenantIdFilter.HasValue && !access.HasGlobalAccess && !access.AllowedTenantIds.Contains(tenantIdFilter.Value))
            {
                return new EmailProcessingListAccessResult(false, []);
            }

            // Build target tenant set from caller scope and optional tenant filter.
            var tenantIds = tenantIdFilter.HasValue
                ? [tenantIdFilter.Value]
                : access.HasGlobalAccess
                    ? _tenantManagerService.GetTenants().Select(t => t.TenantId).ToArray()
                    : access.AllowedTenantIds.ToArray();

            var statuses = new List<EmailProcessingStatusSummary>();
            foreach (var tenantId in tenantIds)
            {
                statuses.AddRange(ListProcessingStatusesForTenant(tenantId));
            }

            IReadOnlyList<EmailProcessingStatusSummary> ordered = statuses
                .OrderByDescending(status => status.UpdatedAt ?? DateTime.MinValue)
                .ThenByDescending(status => status.ProcessingId)
                .Take(200)
                .ToList();

            return new EmailProcessingListAccessResult(true, ordered);
        }

        /// <inheritdoc />
        public async Task<EmailProcessingDetailsAccessResult> TryGetProcessingDetailsForAccessAsync(
            ClaimsPrincipal user,
            Guid tenantId,
            Guid processingId)
        {
            var access = await ResolveEmailAccessAsync(user);
            if (!access.Allowed)
            {
                return new EmailProcessingDetailsAccessResult(false, false, null);
            }

            // Tenant-scoped details are visible to global admins or managers in that tenant.
            if (!access.HasGlobalAccess && !access.AllowedTenantIds.Contains(tenantId))
            {
                return new EmailProcessingDetailsAccessResult(false, false, null);
            }

            var details = TryReadProcessingDetails(tenantId, processingId);
            return new EmailProcessingDetailsAccessResult(true, details != null, details);
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

        /// <inheritdoc />
        public async Task<EmailAccessScope> ResolveEmailAccessAsync(ClaimsPrincipal user)
        {
            if (user.Identity?.IsAuthenticated != true)
            {
                return new EmailAccessScope(false, false, []);
            }

            string? userEmail = user.GetEmail();
            if (string.IsNullOrEmpty(userEmail))
            {
                return new EmailAccessScope(false, false, []);
            }

            if (await _securityGroupService.IsUserInGroupAsync(userEmail, SecurityGroupNameBuilder.GlobalAdminsGroup))
            {
                return new EmailAccessScope(true, true, []);
            }

            var allowedTenantIds = new HashSet<Guid>();
            foreach (var tenant in _tenantManagerService.GetTenants())
            {
                if (await _securityGroupService.IsUserInGroupAsync(userEmail, SecurityGroupNameBuilder.TenantManager(tenant.TenantId)))
                {
                    allowedTenantIds.Add(tenant.TenantId);
                }
            }

            return new EmailAccessScope(allowedTenantIds.Count > 0, false, allowedTenantIds);
        }

        /// <inheritdoc />
        public bool CanAccessFilename(string filename, EmailAccessScope accessScope)
        {
            if (accessScope.HasGlobalAccess)
            {
                return true;
            }

            return TryExtractTenantIdFromFilename(filename, out var fileTenantId) &&
                   accessScope.AllowedTenantIds.Contains(fileTenantId);
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
        private (TenantRoutingStatus Status, Guid? TenantId, Guid? RoadId, bool HasSubdomain) ResolveTenantIdFromRecipients(SendGridInboundEmail email)
        {
            bool foundRecipientToken = false;
            bool foundReadableAddress = false;

            foreach (var recipient in EnumerateRecipients(email))
            {
                foundRecipientToken = true;

                foreach (var routingToken in EnumerateRoutingTokens(recipient))
                {
                    foundReadableAddress = true;

                    // tenant.road format (dot delimiter) routes directly to a road.
                    var routeParts = routingToken.LocalPart.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (routeParts.Length == 2 &&
                        Guid.TryParse(routeParts[0], out var tenantIdFromPair) &&
                        Guid.TryParse(routeParts[1], out var roadIdFromPair))
                    {
                        var tenant = _tenantManagerService.GetTenant(tenantIdFromPair);
                        bool roadBelongsToTenant = tenant?.Roads.Any(road => road.RoadId == roadIdFromPair) ?? false;
                        if (roadBelongsToTenant)
                        {
                            return (TenantRoutingStatus.MatchedTenantRoad, tenantIdFromPair, roadIdFromPair, routingToken.HasSubdomain);
                        }
                    }

                    // tenant-only format routes to tenant-level processing.
                    if (Guid.TryParse(routingToken.LocalPart, out var tenantId) && _tenantManagerService.GetTenant(tenantId) != null)
                    {
                        return (TenantRoutingStatus.Matched, tenantId, null, routingToken.HasSubdomain);
                    }
                }
            }

            if (!foundRecipientToken || !foundReadableAddress)
            {
                return (TenantRoutingStatus.Unreadable, null, null, false);
            }

            return (TenantRoutingStatus.NoKnownTenant, null, null, false);
        }

        /// <summary>
        /// Creates notification addresses on all roads for a tenant from inbound email content.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <param name="email">The inbound email payload.</param>
        private void CreateTenantRoadAddressesFromEmail(Guid tenantId, SendGridInboundEmail email)
        {
            var tenant = _tenantManagerService.GetTenant(tenantId);
            if (tenant == null)
            {
                _logger.LogWarning("Could not create tenant-wide addresses: tenant {TenantId} was not found", tenantId);
                return;
            }

            foreach (var road in tenant.Roads)
            {
                CreateRoadAddressFromEmail(tenantId, road.RoadId, email);
            }
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

                if (!_tenantManagerService.TryAppendAddressToRoad(tenantId, roadId, address))
                {
                    _logger.LogWarning("Inbound email route mismatch: road {RoadId} does not belong to tenant {TenantId}", roadId, tenantId);
                    return;
                }

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
        private static IEnumerable<(string LocalPart, bool HasSubdomain)> EnumerateRoutingTokens(string input)
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

                // Split mailbox into local-part and domain.
                int atIndex = cleanToken.IndexOf('@');
                if (atIndex <= 0 || atIndex >= cleanToken.Length - 1)
                {
                    continue;
                }

                var localPart = cleanToken[..atIndex].Trim('"', '\'', '<', '>');
                var domainPart = cleanToken[(atIndex + 1)..].Trim('"', '\'', '<', '>');

                if (string.IsNullOrWhiteSpace(localPart) || string.IsNullOrWhiteSpace(domainPart))
                {
                    continue;
                }

                // Subdomain exists when domain has at least three labels (e.g. notifications.roadstothere.com).
                bool hasSubdomain = domainPart.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length >= 3;
                yield return (localPart, hasSubdomain);
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

        /// <summary>
        /// Lists processing status summaries for one tenant by reading state artifacts.
        /// </summary>
        /// <param name="tenantId">The tenant identifier.</param>
        /// <returns>Processing status summaries discovered under tenant email artifacts.</returns>
        private IReadOnlyList<EmailProcessingStatusSummary> ListProcessingStatusesForTenant(Guid tenantId)
        {
            var tenantEmailStoragePath = GetTenantEmailStoragePath(tenantId);
            if (!Directory.Exists(tenantEmailStoragePath))
            {
                return [];
            }

            var summaries = new List<EmailProcessingStatusSummary>();

            // Processing artifacts live at: {tenantRoot}/emails/{processingId}/processing/state.json
            foreach (var processingDir in Directory.GetDirectories(tenantEmailStoragePath))
            {
                var folderName = Path.GetFileName(processingDir);
                if (!Guid.TryParseExact(folderName, "N", out var processingId))
                {
                    continue;
                }

                var statePath = Path.Combine(processingDir, "processing", "state.json");
                if (!File.Exists(statePath))
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(statePath);
                    using var document = JsonDocument.Parse(stream);
                    var root = document.RootElement;

                    string status = root.TryGetProperty("status", out var statusNode) && statusNode.ValueKind == JsonValueKind.String
                        ? statusNode.GetString() ?? "unknown"
                        : "unknown";

                    int attempt = root.TryGetProperty("attempt", out var attemptNode) && attemptNode.TryGetInt32(out var parsedAttempt)
                        ? parsedAttempt
                        : 0;

                    int createdCount = root.TryGetProperty("createdCount", out var createdNode) && createdNode.TryGetInt32(out var parsedCreated)
                        ? parsedCreated
                        : 0;

                    int droppedCount = root.TryGetProperty("droppedCount", out var droppedNode) && droppedNode.TryGetInt32(out var parsedDropped)
                        ? parsedDropped
                        : 0;

                    DateTime? updatedAt = null;
                    if (root.TryGetProperty("updatedAt", out var updatedNode) && updatedNode.ValueKind == JsonValueKind.String)
                    {
                        var rawUpdated = updatedNode.GetString();
                        if (DateTime.TryParse(rawUpdated, out var parsedUpdated))
                        {
                            updatedAt = parsedUpdated;
                        }
                    }
                    else if (root.TryGetProperty("queuedAt", out var queuedNode) && queuedNode.ValueKind == JsonValueKind.String)
                    {
                        var rawQueued = queuedNode.GetString();
                        if (DateTime.TryParse(rawQueued, out var parsedQueued))
                        {
                            updatedAt = parsedQueued;
                        }
                    }

                    summaries.Add(new EmailProcessingStatusSummary(
                        TenantId: tenantId,
                        ProcessingId: processingId,
                        Status: status,
                        Attempt: attempt,
                        CreatedCount: createdCount,
                        DroppedCount: droppedCount,
                        UpdatedAt: updatedAt));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to read processing state for tenant {TenantId}, processing id {ProcessingId}",
                        tenantId,
                        folderName);
                }
            }

            return summaries;
        }

        /// <summary>
        /// Attempts to read processing details for one processing artifact folder.
        /// </summary>
        /// <param name="tenantId">The tenant that owns the artifact.</param>
        /// <param name="processingId">The processing identifier folder.</param>
        /// <returns>The artifact JSON payloads when found; otherwise <see langword="null"/>.</returns>
        private EmailProcessingDetails? TryReadProcessingDetails(Guid tenantId, Guid processingId)
        {
            var artifactRoot = Path.Combine(GetTenantEmailStoragePath(tenantId), processingId.ToString("N"));
            var fullArtifactRoot = Path.GetFullPath(artifactRoot);
            var tenantRoot = Path.GetFullPath(GetTenantEmailStoragePath(tenantId));

            // Defense-in-depth: ensure caller-provided identifiers cannot escape tenant scope.
            if (!fullArtifactRoot.StartsWith(tenantRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!Directory.Exists(fullArtifactRoot))
            {
                return null;
            }

            var statePath = Path.Combine(fullArtifactRoot, "processing", "state.json");
            if (!File.Exists(statePath))
            {
                return null;
            }

            var createdPath = Path.Combine(fullArtifactRoot, "outputs", "created.json");
            var droppedPath = Path.Combine(fullArtifactRoot, "drops", "dropped.json");

            var stateJson = File.ReadAllText(statePath);
            var createdJson = File.Exists(createdPath) ? File.ReadAllText(createdPath) : "[]";
            var droppedJson = File.Exists(droppedPath) ? File.ReadAllText(droppedPath) : "[]";

            return new EmailProcessingDetails(stateJson, createdJson, droppedJson);
        }

        private async Task InitializeProcessingArtifactAsync(
            Guid tenantId,
            Guid processingId,
            string? storedAs,
            (TenantRoutingStatus Status, Guid? TenantId, Guid? RoadId, bool HasSubdomain) tenantResolution,
            CancellationToken cancellationToken)
        {
            // Nothing to initialize if the encrypted file reference was not produced.
            if (string.IsNullOrWhiteSpace(storedAs))
            {
                return;
            }

            // Keep processing artifacts directly under tenant emails/{processingId}
            // so all data for one inbound email can be inspected together.
            var artifactRoot = Path.Combine(GetTenantEmailStoragePath(tenantId), processingId.ToString("N"));
            var rawPath = Path.Combine(artifactRoot, "raw");
            var processingPath = Path.Combine(artifactRoot, "processing");
            var outputsPath = Path.Combine(artifactRoot, "outputs");
            var dropsPath = Path.Combine(artifactRoot, "drops");

            // Pre-create folders to avoid race conditions when background workers write outputs.
            Directory.CreateDirectory(rawPath);
            Directory.CreateDirectory(processingPath);
            Directory.CreateDirectory(outputsPath);
            Directory.CreateDirectory(dropsPath);

            var options = new JsonSerializerOptions { WriteIndented = true };

            await using (var rawStream = File.Create(Path.Combine(rawPath, "reference.json")))
            {
                // Store immutable routing + storage reference metadata for queue workers.
                await JsonSerializer.SerializeAsync(rawStream, new
                {
                    storedAs,
                    routedAt = DateTime.UtcNow,
                    routeStatus = tenantResolution.Status.ToString(),
                    routeRoadId = tenantResolution.RoadId,
                    hasSubdomain = tenantResolution.HasSubdomain
                }, options, cancellationToken);
            }

            await using var stateStream = File.Create(Path.Combine(processingPath, "state.json"));
            // Initial state snapshot consumed by diagnostics and operations.
            await JsonSerializer.SerializeAsync(stateStream, new
            {
                status = "queued",
                attempt = 0,
                queuedAt = DateTime.UtcNow
            }, options, cancellationToken);
        }

        private static bool TryExtractTenantIdFromFilename(string filename, out Guid tenantId)
        {
            tenantId = Guid.Empty;
            if (string.IsNullOrWhiteSpace(filename))
            {
                return false;
            }

            var normalized = filename.Replace('\\', '/').Trim();
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 2 && Guid.TryParse(parts[0], out tenantId);
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
