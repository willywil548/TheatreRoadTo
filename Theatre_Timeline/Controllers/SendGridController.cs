using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        private readonly ISendGridEmailService _sendGridEmailService;

        public SendGridController(
            ILogger<SendGridController> logger,
            ISendGridEmailService sendGridEmailService)
        {
            _logger = logger;
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
                    processingId = result.ProcessingId,
                    queuedForProcessing = result.QueuedForProcessing,
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
        /// Requires Global Admin or Tenant Manager (for own tenant files).
        /// </summary>
        /// <param name="filename">The encrypted email filename.</param>
        [HttpGet("email/{filename}")]
        [Authorize]
        public async Task<IActionResult> GetEmail(string filename)
        {
            try
            {
                var getResult = await _sendGridEmailService.TryGetStoredEmailForAccessAsync(User, filename);
                if (!getResult.Allowed)
                {
                    _logger.LogWarning("Unauthorized access attempt to email by user");
                    return Forbid();
                }

                var result = getResult.EmailResult;

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
        /// Lists stored encrypted emails.
        /// Endpoint: GET /api/sendgrid/emails?tenantId={tenantId}
        /// Requires Global Admin or Tenant Manager (own tenant files only).
        /// </summary>
        /// <param name="tenantId">Optional tenant filter. If omitted, returns all emails the caller can access.</param>
        [HttpGet("emails")]
        [Authorize]
        public async Task<IActionResult> ListEmails([FromQuery] Guid? tenantId = null)
        {
            try
            {
                var listResult = await _sendGridEmailService.TryListStoredEmailsForAccessAsync(User, tenantId);
                if (!listResult.Allowed)
                {
                    _logger.LogWarning("Unauthorized access attempt to email list");
                    return Forbid();
                }

                return Ok(new { count = listResult.Emails.Count, emails = listResult.Emails });
            }
            catch (Exception ex)
            {
                var errorId = Guid.NewGuid().ToString();
                _logger.LogError(ex, "Error listing emails. ErrorId: {ErrorId}", errorId);
                return StatusCode(500, new { error = "Failed to list emails", errorId });
            }
        }

        /// <summary>
        /// Lists asynchronous processing status artifacts for inbound emails.
        /// Endpoint: GET /api/sendgrid/processing?tenantId={tenantId}
        /// Requires Global Admin or Tenant Manager (own tenant artifacts only).
        /// </summary>
        /// <param name="tenantId">Optional tenant filter. If omitted, returns all processing statuses the caller can access.</param>
        [HttpGet("processing")]
        [Authorize]
        public async Task<IActionResult> ListProcessingStatuses([FromQuery] Guid? tenantId = null)
        {
            try
            {
                var result = await _sendGridEmailService.TryListProcessingStatusesForAccessAsync(User, tenantId);
                if (!result.Allowed)
                {
                    _logger.LogWarning("Unauthorized access attempt to processing status list");
                    return Forbid();
                }

                return Ok(new { count = result.Statuses.Count, statuses = result.Statuses });
            }
            catch (Exception ex)
            {
                var errorId = Guid.NewGuid().ToString();
                _logger.LogError(ex, "Error listing processing statuses. ErrorId: {ErrorId}", errorId);
                return StatusCode(500, new { error = "Failed to list processing statuses", errorId });
            }
        }

        /// <summary>
        /// Gets detailed processing artifacts for a specific inbound email processing run.
        /// Endpoint: GET /api/sendgrid/processing/{tenantId}/{processingId}
        /// Requires Global Admin or Tenant Manager (own tenant artifacts only).
        /// </summary>
        /// <param name="tenantId">The tenant ID that owns the processing artifact.</param>
        /// <param name="processingId">The processing ID created at email ingestion time.</param>
        [HttpGet("processing/{tenantId:guid}/{processingId:guid}")]
        [Authorize]
        public async Task<IActionResult> GetProcessingDetails(Guid tenantId, Guid processingId)
        {
            try
            {
                var result = await _sendGridEmailService.TryGetProcessingDetailsForAccessAsync(User, tenantId, processingId);
                if (!result.Allowed)
                {
                    _logger.LogWarning("Unauthorized access attempt to processing details. TenantId: {TenantId}, ProcessingId: {ProcessingId}", tenantId, processingId);
                    return Forbid();
                }

                if (!result.Found || result.Details == null)
                {
                    return NotFound(new { error = "Processing details not found" });
                }

                return Ok(result.Details);
            }
            catch (Exception ex)
            {
                var errorId = Guid.NewGuid().ToString();
                _logger.LogError(ex, "Error retrieving processing details. ErrorId: {ErrorId}", errorId);
                return StatusCode(500, new { error = "Failed to retrieve processing details", errorId });
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
    }
}