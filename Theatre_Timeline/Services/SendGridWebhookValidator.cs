using System.Text.Json.Serialization;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Service for capturing webhook headers and basic validation.
    /// </summary>
    public interface ISendGridWebhookValidator
    {
        /// <summary>
        /// Captures headers and performs basic validation on the incoming request.
        /// </summary>
        /// <param name="context">The HTTP context of the request.</param>
        /// <returns>Validation result with captured headers.</returns>
        Task<WebhookValidationResult> ValidateRequestAsync(HttpContext context);
    }

    /// <summary>
    /// Result of webhook validation.
    /// </summary>
    public class WebhookValidationResult
    {
        public bool IsValid { get; set; }
        public string? Reason { get; set; }
        public WebhookHeaders? Headers { get; set; }

        public static WebhookValidationResult Success(WebhookHeaders? headers = null) => new()
        {
            IsValid = true,
            Headers = headers
        };

        public static WebhookValidationResult Failure(string reason, WebhookHeaders? headers = null) => new()
        {
            IsValid = false,
            Reason = reason,
            Headers = headers
        };
    }

    /// <summary>
    /// Captured webhook headers for logging, validation, and audit purposes.
    /// </summary>
    public class WebhookHeaders
    {
        // === Twilio/SendGrid Event Headers ===

        [JsonPropertyName("twilioSignature")]
        public string? TwilioSignature { get; set; }

        [JsonPropertyName("twilioTimestamp")]
        public string? TwilioTimestamp { get; set; }

        // === SendGrid Specific Headers ===

        [JsonPropertyName("sgEventId")]
        public string? SendGridEventId { get; set; }

        [JsonPropertyName("sgId")]
        public string? SendGridId { get; set; }

        [JsonPropertyName("sgMessageId")]
        public string? SendGridMessageId { get; set; }

        [JsonPropertyName("sgContentType")]
        public string? SendGridContentType { get; set; }

        // === Network/Request Headers ===

        [JsonPropertyName("clientIp")]
        public string? ClientIp { get; set; }

        [JsonPropertyName("forwardedFor")]
        public string? ForwardedFor { get; set; }

        [JsonPropertyName("forwardedProto")]
        public string? ForwardedProto { get; set; }

        [JsonPropertyName("forwardedHost")]
        public string? ForwardedHost { get; set; }

        [JsonPropertyName("realIp")]
        public string? RealIp { get; set; }

        // === Standard HTTP Headers ===

        [JsonPropertyName("userAgent")]
        public string? UserAgent { get; set; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; set; }

        [JsonPropertyName("contentLength")]
        public string? ContentLength { get; set; }

        [JsonPropertyName("host")]
        public string? Host { get; set; }

        [JsonPropertyName("accept")]
        public string? Accept { get; set; }

        // === All Other Headers (for discovery) ===

        [JsonPropertyName("allHeaders")]
        public Dictionary<string, string>? AllHeaders { get; set; }

        // === Metadata ===

        [JsonPropertyName("capturedAt")]
        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("requestPath")]
        public string? RequestPath { get; set; }

        [JsonPropertyName("requestMethod")]
        public string? RequestMethod { get; set; }
    }

    /// <summary>
    /// Implementation that captures all headers for analysis.
    /// </summary>
    public class SendGridWebhookValidator : ISendGridWebhookValidator
    {
        private readonly ILogger<SendGridWebhookValidator> _logger;
        private readonly IConfiguration _configuration;

        public SendGridWebhookValidator(
            ILogger<SendGridWebhookValidator> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public Task<WebhookValidationResult> ValidateRequestAsync(HttpContext context)
        {
            // Capture all headers
            var headers = CaptureHeaders(context);
            
            // Log headers for analysis
            LogHeaders(headers);

            // For now, allow all requests but capture everything
            // We can analyze the headers from saved emails to determine validation strategy
            var allowDevelopmentBypass = _configuration.GetValue<bool>("SendGrid:AllowDevelopmentBypass", true);
            
            if (allowDevelopmentBypass)
            {
                _logger.LogInformation("Development bypass enabled - accepting request and capturing headers for analysis");
                return Task.FromResult(WebhookValidationResult.Success(headers));
            }

            // Basic validation: check if we have any indication this is from SendGrid
            // For Inbound Parse, SendGrid uses "Sendlib/1.0" as the User-Agent
            bool isSendlibUserAgent = headers.UserAgent?.Contains("Sendlib", StringComparison.OrdinalIgnoreCase) ?? false;
            
            bool hasSendGridIndicators = 
                isSendlibUserAgent ||
                !string.IsNullOrEmpty(headers.SendGridEventId) ||
                !string.IsNullOrEmpty(headers.SendGridId) ||
                !string.IsNullOrEmpty(headers.SendGridMessageId);

            if (hasSendGridIndicators)
            {
                _logger.LogInformation("SendGrid indicators found - User-Agent: {UserAgent}", headers.UserAgent);
                return Task.FromResult(WebhookValidationResult.Success(headers));
            }

            _logger.LogWarning("No SendGrid indicators found in request headers");
            return Task.FromResult(WebhookValidationResult.Failure("No SendGrid indicators in headers", headers));
        }

        /// <summary>
        /// Captures ALL headers from the request for analysis.
        /// </summary>
        private WebhookHeaders CaptureHeaders(HttpContext context)
        {
            var requestHeaders = context.Request.Headers;
            
            // Capture all headers into a dictionary
            var allHeaders = new Dictionary<string, string>();
            foreach (var header in requestHeaders)
            {
                // Skip very long headers (like cookies) to avoid bloating storage
                var value = header.Value.ToString();
                if (value.Length <= 2000)
                {
                    allHeaders[header.Key] = value;
                }
                else
                {
                    allHeaders[header.Key] = $"[TRUNCATED - {value.Length} chars]";
                }
            }

            return new WebhookHeaders
            {
                // Twilio/SendGrid specific
                TwilioSignature = requestHeaders["X-Twilio-Email-Event-Webhook-Signature"].FirstOrDefault(),
                TwilioTimestamp = requestHeaders["X-Twilio-Email-Event-Webhook-Timestamp"].FirstOrDefault(),
                SendGridEventId = requestHeaders["X-SG-EID"].FirstOrDefault(),
                SendGridId = requestHeaders["X-SG-ID"].FirstOrDefault(),
                SendGridMessageId = requestHeaders["X-SG-Message-ID"].FirstOrDefault(),
                SendGridContentType = requestHeaders["X-SG-Content-Type"].FirstOrDefault(),

                // Network headers
                ClientIp = GetClientIpAddress(context),
                ForwardedFor = requestHeaders["X-Forwarded-For"].FirstOrDefault(),
                ForwardedProto = requestHeaders["X-Forwarded-Proto"].FirstOrDefault(),
                ForwardedHost = requestHeaders["X-Forwarded-Host"].FirstOrDefault(),
                RealIp = requestHeaders["X-Real-IP"].FirstOrDefault(),

                // Standard HTTP headers
                UserAgent = requestHeaders["User-Agent"].FirstOrDefault(),
                ContentType = requestHeaders["Content-Type"].FirstOrDefault(),
                ContentLength = requestHeaders["Content-Length"].FirstOrDefault(),
                Host = requestHeaders["Host"].FirstOrDefault(),
                Accept = requestHeaders["Accept"].FirstOrDefault(),

                // All headers for discovery
                AllHeaders = allHeaders,

                // Metadata
                CapturedAt = DateTime.UtcNow,
                RequestPath = context.Request.Path.Value,
                RequestMethod = context.Request.Method
            };
        }

        /// <summary>
        /// Logs all captured headers.
        /// </summary>
        private void LogHeaders(WebhookHeaders headers)
        {
            _logger.LogInformation(
                "=== WEBHOOK HEADERS CAPTURED ===\n" +
                "Time: {CapturedAt}\n" +
                "Method: {Method} {Path}\n" +
                "ClientIP: {ClientIp}\n" +
                "X-Forwarded-For: {ForwardedFor}\n" +
                "Host: {Host}\n" +
                "User-Agent: {UserAgent}\n" +
                "Content-Type: {ContentType}\n" +
                "Content-Length: {ContentLength}\n" +
                "--- SendGrid Headers ---\n" +
                "X-SG-EID: {SgEid}\n" +
                "X-SG-ID: {SgId}\n" +
                "X-SG-Message-ID: {SgMessageId}\n" +
                "X-SG-Content-Type: {SgContentType}\n" +
                "--- Twilio Headers ---\n" +
                "X-Twilio-Signature: {TwilioSig}\n" +
                "X-Twilio-Timestamp: {TwilioTs}\n" +
                "================================",
                headers.CapturedAt,
                headers.RequestMethod, headers.RequestPath,
                headers.ClientIp ?? "n/a",
                headers.ForwardedFor ?? "n/a",
                headers.Host ?? "n/a",
                headers.UserAgent ?? "n/a",
                headers.ContentType ?? "n/a",
                headers.ContentLength ?? "n/a",
                headers.SendGridEventId ?? "n/a",
                headers.SendGridId ?? "n/a",
                headers.SendGridMessageId ?? "n/a",
                headers.SendGridContentType ?? "n/a",
                !string.IsNullOrEmpty(headers.TwilioSignature) ? "[PRESENT]" : "n/a",
                headers.TwilioTimestamp ?? "n/a");

            // Log all headers at debug level
            if (headers.AllHeaders != null)
            {
                _logger.LogDebug("All headers: {@AllHeaders}", headers.AllHeaders);
            }
        }

        /// <summary>
        /// Gets the client IP address from the request.
        /// </summary>
        private static string? GetClientIpAddress(HttpContext context)
        {
            // Check X-Forwarded-For header first (common when behind a proxy/load balancer)
            var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrEmpty(forwardedFor))
            {
                var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (ips.Length > 0)
                {
                    return ips[0].Trim();
                }
            }

            // Check X-Real-IP header
            var realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrEmpty(realIp))
            {
                return realIp;
            }

            // Fall back to remote IP address
            return context.Connection.RemoteIpAddress?.ToString();
        }
    }
}
