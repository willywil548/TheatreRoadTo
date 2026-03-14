using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        private readonly ISecurityGroupService _securityGroupService;
        private readonly ISendGridEmailService _sendGridEmailService;

        public SendGridController(
            ILogger<SendGridController> logger,
            ISecurityGroupService securityGroupService,
            ISendGridEmailService sendGridEmailService)
        {
            _logger = logger;
            _securityGroupService = securityGroupService;
            _sendGridEmailService = sendGridEmailService;
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
                var result = await _sendGridEmailService.ProcessInboundEmailAsync(HttpContext, email);

                if (!result.IsValid)
                {
                    return Unauthorized(new { error = "Invalid webhook source", reason = result.ValidationReason });
                }

                if (!result.Saved)
                {
                    return Ok(new
                    {
                        message = "Email acknowledged but not saved",
                        reason = result.ValidationReason,
                        from = result.From,
                        subject = result.Subject,
                        spamScore = result.SpamScore,
                        saved = result.Saved
                    });
                }

                return Ok(new
                {
                    message = "Email received, validated, encrypted, and saved successfully",
                    file = result.Filename,
                    from = result.From,
                    subject = result.Subject,
                    spamScore = result.SpamScore,
                    validated = result.IsValid,
                    saved = result.Saved
                });
            }
            catch (Exception ex)
            {
                var errorId = Guid.NewGuid().ToString();
                _logger.LogError(ex, "Error processing inbound email from SendGrid. ErrorId: {ErrorId}", errorId);
                return StatusCode(500, new { error = "Failed to process email", errorId });
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
            if (!await IsGlobalAdminAsync())
            {
                _logger.LogWarning("Unauthorized access attempt to email by user");
                return Forbid();
            }

            try
            {
                var result = await _sendGridEmailService.GetStoredEmailAsync(filename);

                return result.Status switch
                {
                    StoredEmailLookupStatus.InvalidFilename => BadRequest(new { error = "Invalid filename format" }),
                    StoredEmailLookupStatus.InvalidPath => BadRequest(new { error = "Invalid path" }),
                    StoredEmailLookupStatus.NotFound => NotFound(new { error = "Email file not found" }),
                    _ => Ok(new
                    {
                        email = result.Email,
                        parsed = new
                        {
                            fromEmail = result.Email?.GetFromEmail(),
                            fromName = result.Email?.GetFromDisplayName(),
                            toEmail = result.Email?.GetToEmail(),
                            bodyText = result.Email?.GetTextBody(),
                            bodyContent = result.Email?.GetBodyContent(),
                            isDkimValid = result.Email?.IsDkimValid(),
                            isSpfValid = result.Email?.IsSpfValid(),
                            isSpam = result.Email?.IsLikelySpam()
                        }
                    })
                };
            }
            catch (Exception ex)
            {
                var errorId = Guid.NewGuid().ToString();
                _logger.LogError(ex, "Error retrieving email. ErrorId: {ErrorId}", errorId);
                return StatusCode(500, new { error = "Failed to retrieve email", errorId });
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
            if (!await IsGlobalAdminAsync())
            {
                _logger.LogWarning("Unauthorized access attempt to email list");
                return Forbid();
            }

            try
            {
                var files = await _sendGridEmailService.ListStoredEmailsAsync();
                return Ok(new { count = files.Count, emails = files });
            }
            catch (Exception ex)
            {
                var errorId = Guid.NewGuid().ToString();
                _logger.LogError(ex, "Error listing emails. ErrorId: {ErrorId}", errorId);
                return StatusCode(500, new { error = "Failed to list emails", errorId });
            }
        }

        /// <summary>
        /// Health check endpoint to verify the webhook is accessible.
        /// </summary>
        [HttpGet("health")]
        [AllowAnonymous]
        public IActionResult HealthCheck()
        {
            return Ok(_sendGridEmailService.GetHealthStatus());
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