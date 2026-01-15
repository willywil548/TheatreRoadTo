using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Theatre_TimeLine.Services;

namespace Theatre_TimeLine.Controllers
{
    /// <summary>
    /// Controller for receiving incoming emails from SendGrid Inbound Parse webhook.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]  // SendGrid webhooks need anonymous access
    public class SendGridController : ControllerBase
    {
        private readonly ILogger<SendGridController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IEmailEncryptionService _encryptionService;
        private readonly string _emailStoragePath;

        public SendGridController(
            ILogger<SendGridController> logger, 
            IConfiguration configuration,
            IEmailEncryptionService encryptionService)
        {
            _logger = logger;
            _configuration = configuration;
            _encryptionService = encryptionService;

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
        /// SendGrid sends emails as multipart/form-data. This endpoint captures all fields,
        /// encrypts them, and writes to disk for proof of concept.
        /// </remarks>
        [HttpPost("inbound")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ReceiveInboundEmail()
        {
            try
            {
                _logger.LogInformation("Received inbound email from SendGrid");

                // Read all form data from SendGrid
                var form = await Request.ReadFormAsync();
                
                // Create a dictionary to store all email data
                var emailData = new Dictionary<string, object>();

                // Extract common SendGrid fields
                foreach (var key in form.Keys)
                {
                    var value = form[key].ToString();
                    emailData[key] = value;
                }

                // Handle attachments if present
                if (form.Files.Count > 0)
                {
                    var attachments = new List<Dictionary<string, string>>();
                    foreach (var file in form.Files)
                    {
                        attachments.Add(new Dictionary<string, string>
                        {
                            ["filename"] = file.FileName,
                            ["contentType"] = file.ContentType,
                            ["length"] = file.Length.ToString()
                        });
                    }
                    emailData["attachments"] = attachments;
                }

                // Add metadata
                emailData["receivedAt"] = DateTime.UtcNow.ToString("O");
                emailData["encrypted"] = true;

                // Generate filename with timestamp
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                string from = emailData.ContainsKey("from") ? emailData["from"].ToString()!.Replace("<", "").Replace(">", "").Replace("@", "_at_") : "unknown";
                string sanitizedFrom = string.Join("_", from.Split(Path.GetInvalidFileNameChars()));
                string filename = $"email_{timestamp}_{sanitizedFrom}.enc";
                string filePath = Path.Combine(_emailStoragePath, filename);

                // Serialize to JSON
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                };
                string jsonContent = JsonSerializer.Serialize(emailData, options);

                // Encrypt and write to disk
                await _encryptionService.WriteEncryptedFileAsync(filePath, jsonContent);

                _logger.LogInformation("Encrypted email saved to: {FilePath}", filePath);
                _logger.LogInformation("Email from: {From}, Subject: {Subject}",
                    emailData.ContainsKey("from") ? emailData["from"] : "N/A",
                    emailData.ContainsKey("subject") ? emailData["subject"] : "N/A");

                return Ok(new { message = "Email received, encrypted, and saved successfully", file = filename });
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
        /// </summary>
        /// <param name="filename">The encrypted email filename.</param>
        [HttpGet("email/{filename}")]
        public async Task<IActionResult> GetEmail(string filename)
        {
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
                var emailData = JsonSerializer.Deserialize<Dictionary<string, object>>(decryptedContent);

                _logger.LogInformation("Retrieved and decrypted email: {Filename}", safeFilename);

                return Ok(emailData);
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
        /// </summary>
        [HttpGet("emails")]
        public IActionResult ListEmails()
        {
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
        public IActionResult HealthCheck()
        {
            // Get encryption status from configuration
            bool encryptionEnabled = _configuration.GetValue<bool>("SendGrid:EnableEncryption", true);
            
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
                timestamp = DateTime.UtcNow
            });
        }
    }
}