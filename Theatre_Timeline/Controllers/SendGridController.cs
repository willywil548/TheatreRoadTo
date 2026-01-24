using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
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
    public class SendGridController : ControllerBase
    {
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
        public async Task<IActionResult> ReceiveInboundEmail()
        {
            try
            {
                _logger.LogInformation("Received inbound email request from SendGrid");

                // Validate the webhook source
                var validationResult = await _webhookValidator.ValidateRequestAsync(HttpContext);
                
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Webhook validation failed: {Reason}", validationResult.Reason);
                    return Unauthorized(new { error = "Invalid webhook source", reason = validationResult.Reason });
                }

                _logger.LogInformation("Webhook validation passed. Headers captured: {HasHeaders}", 
                    validationResult.Headers != null);

                // Read all form data from SendGrid
                var form = await Request.ReadFormAsync();
                
                // Parse into strongly-typed model
                var email = new SendGridInboundEmail
                {
                    RawEmail = form["email"].ToString(),
                    Charsets = form["charsets"].ToString(),
                    Dkim = form["dkim"].ToString(),
                    SpamScore = form["spam_score"].ToString(),
                    SpamReport = form["spam_report"].ToString(),
                    To = form["to"].ToString(),
                    From = form["from"].ToString(),
                    Subject = form["subject"].ToString(),
                    Envelope = form["envelope"].ToString(),
                    SenderIp = form["sender_ip"].ToString(),
                    Spf = form["SPF"].ToString(),
                    Text = form["text"].ToString(),
                    Html = form["html"].ToString(),
                    Cc = form["cc"].ToString(),
                    AttachmentCount = form["attachments"].ToString(),
                    AttachmentInfo = form["attachment-info"].ToString(),
                    ReceivedAt = DateTime.UtcNow.ToString("O"),
                    Encrypted = true,
                    ValidatedSource = validationResult.IsValid,
                    WebhookHeaders = validationResult.Headers
                };

                // Handle file attachments if present
                if (form.Files.Count > 0)
                {
                    email.Attachments = new List<EmailAttachment>();
                    foreach (var file in form.Files)
                    {
                        email.Attachments.Add(new EmailAttachment
                        {
                            Filename = file.FileName,
                            ContentType = file.ContentType,
                            Length = file.Length
                        });
                    }
                }

                // Log parsed email info
                _logger.LogInformation("Parsed email - From: {From}, To: {To}, Subject: {Subject}",
                    email.GetFromEmail(), email.GetToEmail(), email.Subject);
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

                _logger.LogInformation("Encrypted email saved to: {FilePath}", filePath);

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
                _logger.LogWarning("Unauthorized access attempt to email by user: {User}", User.GetEmail());
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

                _logger.LogInformation("Retrieved and decrypted email: {Filename}", safeFilename);

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
                _logger.LogError(ex, "Error retrieving email: {Filename}", filename);
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
                _logger.LogWarning("Unauthorized access attempt to email list by user: {User}", User.GetEmail());
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
                ? _emailStoragePath.Substring(appBasePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : Path.GetFileName(_emailStoragePath);
            
            return Ok(new 
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
        /// Checks if the current user is a member of the Global Admin (Roads-Admin) group.
        /// </summary>
        private async Task<bool> IsGlobalAdminAsync()
        {
            string? userEmail = User.GetEmail();
            if (string.IsNullOrEmpty(userEmail))
            {
                return false;
            }

            return await _securityGroupService.IsUserInGroupAsync(
                userEmail, 
                SecurityGroupNameBuilder.GlobalAdminsGroup);
        }
    }
}