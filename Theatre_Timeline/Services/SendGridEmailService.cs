using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    public interface ISendGridEmailService
    {
        Task<InboundEmailProcessResult> ProcessInboundEmailAsync(HttpContext context, SendGridInboundEmail email);
        Task<StoredEmailLookupResult> GetStoredEmailAsync(string filename);
        Task<IReadOnlyList<StoredEmailSummary>> ListStoredEmailsAsync();
        SendGridHealthStatus GetHealthStatus();
    }

    public sealed record InboundEmailProcessResult(
        bool IsValid,
        string? ValidationReason,
        string? Filename,
        string? From,
        string? Subject,
        double SpamScore);

    public enum StoredEmailLookupStatus
    {
        Success,
        InvalidFilename,
        InvalidPath,
        NotFound
    }

    public sealed record StoredEmailLookupResult(StoredEmailLookupStatus Status, SendGridInboundEmail? Email = null);

    public sealed record StoredEmailSummary(string Filename, long Size, DateTime Created);

    public sealed record SendGridHealthValidationStatus(bool IpRequired, bool AuthRequired);

    public sealed record SendGridHealthStatus(
        string Status,
        string StoragePath,
        string Encryption,
        SendGridHealthValidationStatus Validation,
        DateTime Timestamp);

    public partial class SendGridEmailService : ISendGridEmailService
    {
        [GeneratedRegex(@"[\r\n\t\x00-\x1F\x7F]", RegexOptions.Compiled)]
        private static partial Regex LogSanitizationRegex();

        private readonly ILogger<SendGridEmailService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IEmailEncryptionService _encryptionService;
        private readonly ISendGridWebhookValidator _webhookValidator;
        private readonly string _emailStoragePath;

        public SendGridEmailService(
            ILogger<SendGridEmailService> logger,
            IConfiguration configuration,
            IEmailEncryptionService encryptionService,
            ISendGridWebhookValidator webhookValidator)
        {
            _logger = logger;
            _configuration = configuration;
            _encryptionService = encryptionService;
            _webhookValidator = webhookValidator;

            string? emailPath = _configuration.GetValue<string>("SendGrid:EmailStoragePath");
            if (string.IsNullOrEmpty(emailPath))
            {
                emailPath = "./emails";
            }

            if (!Path.IsPathRooted(emailPath))
            {
                emailPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    emailPath.Trim(['.', '\\', '/']));
            }

            _emailStoragePath = emailPath;

            if (!Directory.Exists(_emailStoragePath))
            {
                Directory.CreateDirectory(_emailStoragePath);
                _logger.LogInformation("Created email storage directory: {Path}", _emailStoragePath);
            }
        }

        public async Task<InboundEmailProcessResult> ProcessInboundEmailAsync(HttpContext context, SendGridInboundEmail email)
        {
            _logger.LogInformation("Received inbound email request from SendGrid");

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

            email.ReceivedAt = DateTime.UtcNow.ToString("O");
            email.Encrypted = true;
            email.ValidatedSource = validationResult.IsValid;
            email.WebhookHeaders = validationResult.Headers;

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

            _logger.LogInformation("Parsed email - From: {From}, To: {To}, Subject: {Subject}",
                MaskEmail(email.GetFromEmail()),
                MaskEmail(email.GetToEmail()),
                SanitizeForLog(email.Subject));
            _logger.LogInformation("Email validation - DKIM: {Dkim}, SPF: {Spf}, Spam Score: {SpamScore}",
                email.IsDkimValid(), email.IsSpfValid(), email.GetSpamScoreValue());

            if (email.IsLikelySpam())
            {
                _logger.LogWarning("Email flagged as spam (score: {SpamScore}), saving but marking as spam",
                    email.GetSpamScoreValue());
            }

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string fromEmail = email.GetFromEmail()?.Replace("@", "_at_") ?? "unknown";
            string sanitizedFrom = string.Join("_", fromEmail.Split(Path.GetInvalidFileNameChars()));
            string filename = $"email_{timestamp}_{sanitizedFrom}_{Guid.NewGuid()}.enc";
            string filePath = Path.Combine(_emailStoragePath, filename);

            email.StoredAs = filename;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string jsonContent = JsonSerializer.Serialize(email, options);
            await _encryptionService.WriteEncryptedFileAsync(filePath, jsonContent);

            _logger.LogInformation("Encrypted email saved successfully");

            return new InboundEmailProcessResult(
                IsValid: true,
                ValidationReason: null,
                Filename: filename,
                From: email.GetFromEmail(),
                Subject: email.Subject,
                SpamScore: email.GetSpamScoreValue());
        }

        public async Task<StoredEmailLookupResult> GetStoredEmailAsync(string filename)
        {
            var safeFilename = Path.GetFileName(filename);
            if (string.IsNullOrEmpty(safeFilename) ||
                !safeFilename.StartsWith("email_", StringComparison.Ordinal) ||
                !safeFilename.EndsWith(".enc", StringComparison.Ordinal))
            {
                return new StoredEmailLookupResult(StoredEmailLookupStatus.InvalidFilename);
            }

            var filePath = Path.Combine(_emailStoragePath, safeFilename);
            var fullPath = Path.GetFullPath(filePath);
            var allowedPath = Path.GetFullPath(_emailStoragePath);

            if (!fullPath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Path traversal attempt detected");
                return new StoredEmailLookupResult(StoredEmailLookupStatus.InvalidPath);
            }

            if (!File.Exists(fullPath))
            {
                return new StoredEmailLookupResult(StoredEmailLookupStatus.NotFound);
            }

            var decryptedContent = await _encryptionService.ReadEncryptedFileAsync(fullPath);
            var email = JsonSerializer.Deserialize<SendGridInboundEmail>(decryptedContent);

            _logger.LogInformation("Retrieved and decrypted email successfully");
            return new StoredEmailLookupResult(StoredEmailLookupStatus.Success, email);
        }

        public Task<IReadOnlyList<StoredEmailSummary>> ListStoredEmailsAsync()
        {
            IReadOnlyList<StoredEmailSummary> files = Directory
                .GetFiles(_emailStoragePath, "email_*.enc")
                .Select(path =>
                {
                    var fileInfo = new FileInfo(path);
                    return new StoredEmailSummary(
                        Filename: Path.GetFileName(path),
                        Size: fileInfo.Length,
                        Created: File.GetCreationTimeUtc(path));
                })
                .OrderByDescending(file => file.Created)
                .ToList();

            return Task.FromResult(files);
        }

        public SendGridHealthStatus GetHealthStatus()
        {
            bool encryptionEnabled = _configuration.GetValue<bool>("SendGrid:EnableEncryption", true);
            bool requireIpValidation = _configuration.GetValue<bool>("SendGrid:RequireIpValidation", false);
            bool requireAuthValidation = _configuration.GetValue<bool>("SendGrid:RequireAuthValidation", false);

            string appBasePath = AppDomain.CurrentDomain.BaseDirectory;
            string relativePath = _emailStoragePath.StartsWith(appBasePath)
                ? _emailStoragePath[appBasePath.Length..]
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : Path.GetFileName(_emailStoragePath);

            return new SendGridHealthStatus(
                Status: "healthy",
                StoragePath: relativePath,
                Encryption: encryptionEnabled ? "enabled" : "disabled",
                Validation: new SendGridHealthValidationStatus(requireIpValidation, requireAuthValidation),
                Timestamp: DateTime.UtcNow);
        }

        private static string SanitizeForLog(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            var sanitized = LogSanitizationRegex().Replace(input, " ");
            const int maxLogLength = 200;
            if (sanitized.Length > maxLogLength)
            {
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
