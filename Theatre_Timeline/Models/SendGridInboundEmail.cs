using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MimeKit;
using Theatre_TimeLine.Services;

namespace Theatre_TimeLine.Models
{
    /// <summary>
    /// Represents an inbound email received from SendGrid Inbound Parse webhook.
    /// Maps to the multipart/form-data fields sent by SendGrid.
    /// Supports both form binding (via BindProperty) and JSON serialization (via JsonPropertyName).
    /// </summary>
    public class SendGridInboundEmail
    {
        private static readonly Regex addressEmailRegex = new(@"<([^>]+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The raw email content including all headers and MIME parts.
        /// This is the complete email as received by SendGrid.
        /// </summary>
        [BindProperty(Name = "email")]
        [JsonPropertyName("email")]
        public string? RawEmail { get; set; }

        /// <summary>
        /// Character sets used in the email as JSON string.
        /// Example: {"to":"UTF-8","from":"UTF-8","subject":"UTF-8"}
        /// </summary>
        [BindProperty(Name = "charsets")]
        [JsonPropertyName("charsets")]
        public string? Charsets { get; set; }

        /// <summary>
        /// The DKIM verification result.
        /// Example: "{@outlook.com : pass}"
        /// </summary>
        [BindProperty(Name = "dkim")]
        [JsonPropertyName("dkim")]
        public string? Dkim { get; set; }

        /// <summary>
        /// The spam score from SpamAssassin.
        /// Lower is better. Typically spam if > 5.
        /// </summary>
        [BindProperty(Name = "spam_score")]
        [JsonPropertyName("spam_score")]
        public string? SpamScore { get; set; }

        /// <summary>
        /// The spam report from SpamAssassin with detailed analysis.
        /// </summary>
        [BindProperty(Name = "spam_report")]
        [JsonPropertyName("spam_report")]
        public string? SpamReport { get; set; }

        /// <summary>
        /// The email address the email was sent to.
        /// Example: "test@your.domain.com" <test@your.domain.com>
        /// </summary>
        [BindProperty(Name = "to")]
        [JsonPropertyName("to")]
        public string? To { get; set; }

        /// <summary>
        /// The sender's email address with display name.
        /// Example: "Robert Wilson <robert.p.wilson@outlook.com>"
        /// </summary>
        [BindProperty(Name = "from")]
        [JsonPropertyName("from")]
        public string? From { get; set; }

        /// <summary>
        /// The email subject line.
        /// </summary>
        [BindProperty(Name = "subject")]
        [JsonPropertyName("subject")]
        public string? Subject { get; set; }

        /// <summary>
        /// The SMTP envelope information as JSON string.
        /// Contains "to" array and "from" string.
        /// </summary>
        [BindProperty(Name = "envelope")]
        [JsonPropertyName("envelope")]
        public string? Envelope { get; set; }

        /// <summary>
        /// The IP address of the sender's mail server.
        /// </summary>
        [BindProperty(Name = "sender_ip")]
        [JsonPropertyName("sender_ip")]
        public string? SenderIp { get; set; }

        /// <summary>
        /// SPF (Sender Policy Framework) verification result.
        /// Values: "pass", "fail", "softfail", "neutral", "none"
        /// </summary>
        [BindProperty(Name = "SPF")]
        [JsonPropertyName("SPF")]
        public string? Spf { get; set; }

        // === Optional fields that may be present ===

        /// <summary>
        /// The plain text body of the email (if sent separately by SendGrid config).
        /// </summary>
        [BindProperty(Name = "text")]
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        /// <summary>
        /// The HTML body of the email (if sent separately by SendGrid config).
        /// </summary>
        [BindProperty(Name = "html")]
        [JsonPropertyName("html")]
        public string? Html { get; set; }

        /// <summary>
        /// CC recipients (if any).
        /// </summary>
        [BindProperty(Name = "cc")]
        [JsonPropertyName("cc")]
        public string? Cc { get; set; }

        /// <summary>
        /// Number of attachments as string.
        /// </summary>
        [BindProperty(Name = "attachments")]
        [JsonPropertyName("attachments")]
        public string? AttachmentCount { get; set; }

        /// <summary>
        /// Attachment metadata as JSON string.
        /// </summary>
        [BindProperty(Name = "attachment-info")]
        [JsonPropertyName("attachment-info")]
        public string? AttachmentInfo { get; set; }

        // === Metadata added by our system (not bound from form) ===

        /// <summary>
        /// UTC timestamp when the email was received by our system.
        /// </summary>
        [BindNever]
        [JsonPropertyName("receivedAt")]
        public string? ReceivedAt { get; set; }

        /// <summary>
        /// Indicates if the email was stored encrypted.
        /// </summary>
        [BindNever]
        [JsonPropertyName("encrypted")]
        public bool Encrypted { get; set; }

        /// <summary>
        /// The filename where the email is stored.
        /// </summary>
        [BindNever]
        [JsonPropertyName("storedAs")]
        public string? StoredAs { get; set; }

        /// <summary>
        /// Whether the email passed webhook validation.
        /// </summary>
        [BindNever]
        [JsonPropertyName("validatedSource")]
        public bool ValidatedSource { get; set; }

        /// <summary>
        /// Captured webhook headers for audit/debugging.
        /// </summary>
        [BindNever]
        [JsonPropertyName("webhookHeaders")]
        public WebhookHeaders? WebhookHeaders { get; set; }

        /// <summary>
        /// List of attachment metadata (populated during processing).
        /// </summary>
        [BindNever]
        [JsonPropertyName("attachmentsList")]
        public List<EmailAttachment>? Attachments { get; set; }

        #region Helper Methods

        /// <summary>
        /// Extracts the email address from the From field.
        /// </summary>
        public string? GetFromEmail()
        {
            if (string.IsNullOrEmpty(this.From))
            {
                return null;
            }

            // Handle format: "Display Name <email@domain.com>"
            var match = addressEmailRegex.Match(this.From);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            // Already just an email address
            return this.From.Trim();
        }

        /// <summary>
        /// Extracts the display name from the From field.
        /// </summary>
        public string? GetFromDisplayName()
        {
            if (string.IsNullOrEmpty(this.From))
            {
                return null;
            }

            var startIndex = this.From.IndexOf('<');
            if (startIndex > 0)
            {
                return From[..startIndex].Trim().Trim('"');
            }

            return null;
        }

        /// <summary>
        /// Extracts the email address from the To field.
        /// </summary>
        public string? GetToEmail()
        {
            if (string.IsNullOrEmpty(this.To))
            {
                return null;
            }

            var match = addressEmailRegex.Match(this.To);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            return this.To.Trim().Trim('"');
        }

        /// <summary>
        /// Gets the spam score as a numeric value.
        /// </summary>
        public double GetSpamScoreValue()
        {
            if (double.TryParse(this.SpamScore, out var score))
            {
                return score;
            }

            return 0;
        }

        /// <summary>
        /// Checks if the email is likely spam based on score.
        /// </summary>
        public bool IsLikelySpam(double threshold = 5.0)
        {
            return GetSpamScoreValue() >= threshold;
        }

        /// <summary>
        /// Checks if DKIM passed verification.
        /// </summary>
        public bool IsDkimValid()
        {
            return !string.IsNullOrEmpty(this.Dkim) &&
                   this.Dkim.Contains("pass", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if SPF passed verification.
        /// </summary>
        public bool IsSpfValid()
        {
            return string.Equals(this.Spf, "pass", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the plain text body from the raw email.
        /// </summary>
        public string? GetTextBody()
        {
            // If text field is populated, use it
            if (!string.IsNullOrEmpty(this.Text))
            {
                return this.Text;
            }

            // Otherwise parse from raw email using MimeKit
            return ExtractBodyFromRawEmail(isHtml: false);
        }

        /// <summary>
        /// Gets the HTML body from the raw email.
        /// </summary>
        public string? GetHtmlBody()
        {
            // If html field is populated, use it
            if (!string.IsNullOrEmpty(this.Html))
            {
                return this.Html;
            }

            // Otherwise parse from raw email using MimeKit
            return ExtractBodyFromRawEmail(isHtml: true);
        }

        /// <summary>
        /// Gets the best available body content (prefers text over HTML).
        /// </summary>
        public string? GetBodyContent()
        {
            var text = GetTextBody();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            var html = GetHtmlBody();
            if (!string.IsNullOrEmpty(html))
            {
                return StripHtmlTags(html);
            }

            return null;
        }

        /// <summary>
        /// Parses the envelope JSON and returns it as a strongly typed object.
        /// </summary>
        public SendGridEnvelope? GetEnvelope()
        {
            if (string.IsNullOrEmpty(this.Envelope))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<SendGridEnvelope>(this.Envelope);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Parses the raw email and returns a MimeMessage for advanced processing.
        /// </summary>
        public MimeMessage? GetParsedMessage()
        {
            if (string.IsNullOrEmpty(this.RawEmail))
            {
                return null;
            }

            try
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(this.RawEmail));
                return MimeMessage.Load(stream);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts body content from raw MIME email using MimeKit.
        /// </summary>
        private string? ExtractBodyFromRawEmail(bool isHtml)
        {
            var message = GetParsedMessage();
            if (message == null)
            {
                return null;
            }

            return isHtml ? message.HtmlBody : message.TextBody;
        }

        /// <summary>
        /// Strips HTML tags from a string.
        /// </summary>
        private static string StripHtmlTags(string html)
        {
            // Remove HTML tags
            var text = Regex.Replace(html, @"<[^>]+>", " ");
            // Decode common HTML entities
            text = text.Replace("&nbsp;", " ")
                       .Replace("&amp;", "&")
                       .Replace("&lt;", "<")
                       .Replace("&gt;", ">")
                       .Replace("&quot;", "\"");
            // Normalize whitespace
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        #endregion
    }

    /// <summary>
    /// Represents email attachment metadata.
    /// </summary>
    public class EmailAttachment
    {
        [JsonPropertyName("filename")]
        public string? Filename { get; set; }

        [JsonPropertyName("contentType")]
        public string? ContentType { get; set; }

        [JsonPropertyName("length")]
        public long Length { get; set; }

        [JsonPropertyName("contentId")]
        public string? ContentId { get; set; }
    }

    /// <summary>
    /// Represents the parsed envelope from SendGrid.
    /// </summary>
    public class SendGridEnvelope
    {
        [JsonPropertyName("to")]
        public List<string>? To { get; set; }

        [JsonPropertyName("from")]
        public string? From { get; set; }
    }
}
