using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Models;
using Theatre_TimeLine.Services;

namespace Theatre_TimeLine.Controllers
{
    /// <summary>
    /// Controller for receiving incoming emails from SendGrid Inbound Parse webhook.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public partial class SendGridController : ControllerBase
    {
        // Regex to sanitize log input - removes newlines and control characters to prevent log injection
        [GeneratedRegex(@"[\r\n\t\x00-\x1F\x7F]", RegexOptions.Compiled)]
        private static partial Regex LogSanitizationRegex();

        private readonly ILogger<SendGridController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IEmailEncryptionService _encryptionService;
        private readonly ISecurityGroupService _securityGroupService;
        private readonly ISendGridWebhookValidator _webhookValidator;
        private readonly string _emailStoragePath;

        public SendGridController(
            ILogger<SendGridController> logger,
            IConfiguration configuration,
            IEmailEncryptionService encryptionService,
            ISecurityGroupService securityGroupService,
            ISendGridWebhookValidator webhookValidator)
        {
            _logger = logger;
            _configuration = configuration;
            _encryptionService = encryptionService;
            _securityGroupService = securityGroupService;
            _webhookValidator = webhookValidator;

            // Get storage path from configuration or use default
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

            // Ensure directory exists
            if (!Directory.Exists(_emailStoragePath))
            {
                Directory.CreateDirectory(_emailStoragePath);
                _logger.LogInformation("Created email storage directory: {Path}", _emailStoragePath);
            }
        }

        /// <summary>
        /// Receives incoming email from SendGrid Inbound Parse webhook.
        /// Endpoint: POST /api/sendgrid/inbound
        /// </summary>
        /// <remarks>
        /// SendGrid sends emails as multipart/form-data. This endpoint validates the source,
        /// parses the email into a strongly-typed model, encrypts, and writes to disk.
        /// </remarks>
        [HttpPost("inbound")]
        [AllowAnonymous]  // SendGrid webhooks need anonymous access
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ReceiveInboundEmail([FromForm] SendGridInboundEmail email)
        {
            try
            {
                _logger.LogInformation("Received inbound email request from SendGrid");

                // Validate the webhook source
                var validationResult = await _webhookValidator.ValidateRequestAsync(HttpContext);

                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Webhook validation failed: {Reason}", SanitizeForLog(validationResult.Reason));
                    return Unauthorized(new { error = "Invalid webhook source", reason = validationResult.Reason });
                }

                _logger.LogInformation("Webhook validation passed. Headers captured: {HasHeaders}",
                    validationResult.Headers != null);

                // Set metadata fields (not bound from form)
                email.ReceivedAt = DateTime.UtcNow.ToString("O");
                email.Encrypted = true;
                email.ValidatedSource = validationResult.IsValid;
                email.WebhookHeaders = validationResult.Headers;

                // Handle file attachments if present
                var form = await Request.ReadFormAsync();
                if (form.Files.Count > 0)
                {
                    email.Attachments = form.Files.Select(file => new EmailAttachment
                    {
                        Filename = file.FileName,
                        ContentType = file.ContentType,
                        Length = file.Length
                    }).ToList();
                }

                // Log parsed email info (sanitized to prevent log injection)
                _logger.LogInformation("Parsed email - From: {From}, To: {To}, Subject: {Subject}",
                    SanitizeForLog(email.GetFromEmail()),
                    SanitizeForLog(email.GetToEmail()),
                    SanitizeForLog(email.Subject));
                _logger.LogInformation("Email validation - DKIM: {Dkim}, SPF: {Spf}, Spam Score: {SpamScore}",
                    email.IsDkimValid(), email.IsSpfValid(), email.GetSpamScoreValue());

                // Check if email is spam
                if (email.IsLikelySpam())
                {
                    _logger.LogWarning("Email flagged as spam (score: {SpamScore}), saving but marking as spam",
                        email.GetSpamScoreValue());
                }

                // Generate filename with timestamp
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                string fromEmail = email.GetFromEmail()?.Replace("@", "_at_") ?? "unknown";
                string sanitizedFrom = string.Join("_", fromEmail.Split(Path.GetInvalidFileNameChars()));
                string filename = $"email_{timestamp}_{sanitizedFrom}.enc";
                string filePath = Path.Combine(_emailStoragePath, filename);

                // Store the filename in the model
                email.StoredAs = filename;

                // Serialize to JSON
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                string jsonContent = JsonSerializer.Serialize(email, options);

                // Encrypt and write to disk
                await _encryptionService.WriteEncryptedFileAsync(filePath, jsonContent);

                _logger.LogInformation("Encrypted email saved successfully");

                return Ok(new
                {
                    message = "Email received, validated, encrypted, and saved successfully",
                    file = filename,
                    from = email.GetFromEmail(),
                    subject = email.Subject,
                    spamScore = email.GetSpamScoreValue(),
                    validated = validationResult.IsValid
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing inbound email from SendGrid");
                return StatusCode(500, new { error = "Failed to process email", details = ex.Message });
            }
        }

        /// <summary>
        /// Retrieves and decrypts a stored email by filename.
        /// Endpoint: GET /api/sendgrid/email/{filename}
        /// Requires Global Admin (Roads-Admin) membership.
        /// </summary>
        /// <param name="filename">The encrypted email filename.</param>
        [HttpGet("email/{filename}")]
        [Authorize]
        public async Task<IActionResult> GetEmail(string filename)
        {
            // Check if user is Global Admin
            if (!await IsGlobalAdminAsync())
            {
                _logger.LogWarning("Unauthorized access attempt to email by user");
                return Forbid();
            }

            try
            {
                // Sanitize filename to prevent directory traversal
                var safeFilename = Path.GetFileName(filename);
                var filePath = Path.Combine(_emailStoragePath, safeFilename);

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound(new { error = "Email file not found" });
                }

                // Read and decrypt
                var decryptedContent = await _encryptionService.ReadEncryptedFileAsync(filePath);
                var email = JsonSerializer.Deserialize<SendGridInboundEmail>(decryptedContent);

                _logger.LogInformation("Retrieved and decrypted email successfully");

                // Return with parsed body content for convenience
                return Ok(new
                {
                    email,
                    parsed = new
                    {
                        fromEmail = email?.GetFromEmail(),
                        fromName = email?.GetFromDisplayName(),
                        toEmail = email?.GetToEmail(),
                        bodyText = email?.GetTextBody(),
                        bodyContent = email?.GetBodyContent(),
                        isDkimValid = email?.IsDkimValid(),
                        isSpfValid = email?.IsSpfValid(),
                        isSpam = email?.IsLikelySpam()
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving email");
                return StatusCode(500, new { error = "Failed to retrieve email", details = ex.Message });
            }
        }

        /// <summary>
        /// Lists all stored encrypted emails.
        /// Endpoint: GET /api/sendgrid/emails
        /// Requires Global Admin (Roads-Admin) membership.
        /// </summary>
        [HttpGet("emails")]
        [Authorize]
        public async Task<IActionResult> ListEmails()
        {
            // Check if user is Global Admin
            if (!await IsGlobalAdminAsync())
            {
                _logger.LogWarning("Unauthorized access attempt to email list");
                return Forbid();
            }

            try
            {
                var files = Directory.GetFiles(_emailStoragePath, "email_*.enc")
                    .Select(f => new
                    {
                        filename = Path.GetFileName(f),
                        size = new FileInfo(f).Length,
                        created = System.IO.File.GetCreationTimeUtc(f)
                    })
                    .OrderByDescending(f => f.created)
                    .ToList();

                return Ok(new { count = files.Count, emails = files });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing emails");
                return StatusCode(500, new { error = "Failed to list emails", details = ex.Message });
            }
        }

        /// <summary>
        /// Health check endpoint to verify the webhook is accessible.
        /// </summary>
        [HttpGet("health")]
        [AllowAnonymous]
        public IActionResult HealthCheck()
        {
            // Get encryption status from configuration
            bool encryptionEnabled = _configuration.GetValue<bool>("SendGrid:EnableEncryption", true);
            bool requireIpValidation = _configuration.GetValue<bool>("SendGrid:RequireIpValidation", false);
            bool requireAuthValidation = _configuration.GetValue<bool>("SendGrid:RequireAuthValidation", false);

            // Convert absolute path to app-relative path for security
            string appBasePath = AppDomain.CurrentDomain.BaseDirectory;
            string relativePath = _emailStoragePath.StartsWith(appBasePath)
                ? _emailStoragePath.Substring(appBasePath.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : Path.GetFileName(_emailStoragePath);

            return Ok(
                new
                {
                    status = "healthy",
                    storagePath = relativePath,
                    encryption = encryptionEnabled ? "enabled" : "disabled",
                    validation = new
                    {
                        ipRequired = requireIpValidation,
                        authRequired = requireAuthValidation
                    },
                    timestamp = DateTime.UtcNow
                });
        }

        /// <summary>
        /// Sanitizes a string for safe logging by removing newlines and control characters.
        /// This prevents log injection attacks.
        /// </summary>
        private static string SanitizeForLog(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            // Remove newlines, tabs, and control characters that could be used for log injection
            var sanitized = LogSanitizationRegex().Replace(input, " ");

            // Truncate to reasonable length to prevent log flooding
            const int maxLogLength = 200;
            if (sanitized.Length > maxLogLength)
            {
                sanitized = string.Concat(sanitized.AsSpan(0, maxLogLength), "...");
            }

            return sanitized;
        }

#pragma warning disable CA1822 // Mark members as static - method accesses instance members via User property
        /// <summary>
        /// Checks if the current user is a member of the Global Admin (Roads-Admin) group.
        /// </summary>
        private async Task<bool> IsGlobalAdminAsync()
        {
            if (!User.Identity?.IsAuthenticated ?? false)
            {
                return false;
            }

            string? userEmail = User.GetEmail();
            if (string.IsNullOrEmpty(userEmail))
            {
                return false;
            }

            return await _securityGroupService.IsUserInGroupAsync(
                userEmail,
                SecurityGroupNameBuilder.GlobalAdminsGroup);
        }
#pragma warning restore CA1822
    }
}