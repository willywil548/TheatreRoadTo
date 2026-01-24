using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Theatre_TimeLine.Services;

namespace Theatre_TimeLine.Models
{
    /// <summary>
    /// Represents an inbound email received from SendGrid Inbound Parse webhook.
    /// Maps to the multipart/form-data fields sent by SendGrid.
    /// </summary>
    public class SendGridInboundEmail
    {
        /// <summary>
        /// The raw email content including all headers and MIME parts.
        /// This is the complete email as received by SendGrid.
        /// </summary>
        [JsonPropertyName("email")]
        public string? RawEmail { get; set; }

        /// <summary>
        /// Character sets used in the email as JSON string.
        /// Example: {"to":"UTF-8","from":"UTF-8","subject":"UTF-8"}
        /// </summary>
        [JsonPropertyName("charsets")]
        public string? Charsets { get; set; }

        /// <summary>
        /// The DKIM verification result.
        /// Example: "{@outlook.com : pass}"
        /// </summary>
        [JsonPropertyName("dkim")]
        public string? Dkim { get; set; }

        /// <summary>
        /// The spam score from SpamAssassin.
        /// Lower is better. Typically spam if > 5.
        /// </summary>
        [JsonPropertyName("spam_score")]
        public string? SpamScore { get; set; }

        /// <summary>
        /// The spam report from SpamAssassin with detailed analysis.
        /// </summary>
        [JsonPropertyName("spam_report")]
        public string? SpamReport { get; set; }

        /// <summary>
        /// The email address the email was sent to.
        /// Example: "test@notifications.roadstothere.com" <test@notifications.roadstothere.com>
        /// </summary>
        [JsonPropertyName("to")]
        public string? To { get; set; }

        /// <summary>
        /// The sender's email address with display name.
        /// Example: "Robert Wilson <robert.p.wilson@outlook.com>"
        /// </summary>
        [JsonPropertyName("from")]
        public string? From { get; set; }

        /// <summary>
        /// The email subject line.
        /// </summary>
        [JsonPropertyName("subject")]
        public string? Subject { get; set; }

        /// <summary>
        /// The SMTP envelope information as JSON string.
        /// Contains "to" array and "from" string.
        /// </summary>
        [JsonPropertyName("envelope")]
        public string? Envelope { get; set; }

        /// <summary>
        /// The IP address of the sender's mail server.
        /// </summary>
        [JsonPropertyName("sender_ip")]
        public string? SenderIp { get; set; }

        /// <summary>
        /// SPF (Sender Policy Framework) verification result.
        /// Values: "pass", "fail", "softfail", "neutral", "none"
        /// </summary>
        [JsonPropertyName("SPF")]
        public string? Spf { get; set; }

        // === Optional fields that may be present ===

        /// <summary>
        /// The plain text body of the email (if sent separately by SendGrid config).
        /// </summary>
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        /// <summary>
        /// The HTML body of the email (if sent separately by SendGrid config).
        /// </summary>
        [JsonPropertyName("html")]
        public string? Html { get; set; }

        /// <summary>
        /// CC recipients (if any).
        /// </summary>
        [JsonPropertyName("cc")]
        public string? Cc { get; set; }

        /// <summary>
        /// Number of attachments as string.
        /// </summary>
        [JsonPropertyName("attachments")]
        public string? AttachmentCount { get; set; }

        /// <summary>
        /// Attachment metadata as JSON string.
        /// </summary>
        [JsonPropertyName("attachment-info")]
        public string? AttachmentInfo { get; set; }

        // === Metadata added by our system ===

        /// <summary>
        /// UTC timestamp when the email was received by our system.
        /// </summary>
        [JsonPropertyName("receivedAt")]
        public string? ReceivedAt { get; set; }

        /// <summary>
        /// Indicates if the email was stored encrypted.
        /// </summary>
        [JsonPropertyName("encrypted")]
        public bool Encrypted { get; set; }

        /// <summary>
        /// The filename where the email is stored.
        /// </summary>
        [JsonPropertyName("storedAs")]
        public string? StoredAs { get; set; }

        /// <summary>
        /// Whether the email passed webhook validation.
        /// </summary>
        [JsonPropertyName("validatedSource")]
        public bool ValidatedSource { get; set; }

        /// <summary>
        /// Captured webhook headers for audit/debugging.
        /// </summary>
        [JsonPropertyName("webhookHeaders")]
        public WebhookHeaders? WebhookHeaders { get; set; }

        /// <summary>
        /// List of attachment metadata (populated during processing).
        /// </summary>
        [JsonPropertyName("attachmentsList")]
        public List<EmailAttachment>? Attachments { get; set; }

        #region Helper Methods

        /// <summary>
        /// Extracts the email address from the From field.
        /// </summary>
        public string? GetFromEmail()
        {
            if (string.IsNullOrEmpty(From))
                return null;

            // Handle format: "Display Name <email@domain.com>"
            var match = Regex.Match(From, @"<([^>]+)>");
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            // Already just an email address
            return From.Trim();
        }

        /// <summary>
        /// Extracts the display name from the From field.
        /// </summary>
        public string? GetFromDisplayName()
        {
            if (string.IsNullOrEmpty(From))
                return null;

            var startIndex = From.IndexOf('<');
            if (startIndex > 0)
            {
                return From.Substring(0, startIndex).Trim().Trim('"');
            }

            return null;
        }

        /// <summary>
        /// Extracts the email address from the To field.
        /// </summary>
        public string? GetToEmail()
        {
            if (string.IsNullOrEmpty(To))
                return null;

            var match = Regex.Match(To, @"<([^>]+)>");
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            return To.Trim().Trim('"');
        }

        /// <summary>
        /// Gets the spam score as a numeric value.
        /// </summary>
        public double GetSpamScoreValue()
        {
            if (double.TryParse(SpamScore, out var score))
                return score;
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
            return !string.IsNullOrEmpty(Dkim) && 
                   Dkim.Contains("pass", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if SPF passed verification.
        /// </summary>
        public bool IsSpfValid()
        {
            return string.Equals(Spf, "pass", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets the plain text body from the raw email.
        /// </summary>
        public string? GetTextBody()
        {
            // If text field is populated, use it
            if (!string.IsNullOrEmpty(Text))
                return Text;

            // Otherwise parse from raw email
            return ExtractBodyFromRawEmail("text/plain");
        }

        /// <summary>
        /// Gets the HTML body from the raw email.
        /// </summary>
        public string? GetHtmlBody()
        {
            // If html field is populated, use it
            if (!string.IsNullOrEmpty(Html))
                return Html;

            // Otherwise parse from raw email
            return ExtractBodyFromRawEmail("text/html");
        }

        /// <summary>
        /// Gets the best available body content (prefers text over HTML).
        /// </summary>
        public string? GetBodyContent()
        {
            var text = GetTextBody();
            if (!string.IsNullOrEmpty(text))
                return text;

            var html = GetHtmlBody();
            if (!string.IsNullOrEmpty(html))
                return StripHtmlTags(html);

            return null;
        }

        /// <summary>
        /// Parses the envelope JSON and returns it as a strongly typed object.
        /// </summary>
        public SendGridEnvelope? GetEnvelope()
        {
            if (string.IsNullOrEmpty(Envelope))
                return null;

            try
            {
                return JsonSerializer.Deserialize<SendGridEnvelope>(Envelope);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts body content from raw MIME email by content type.
        /// </summary>
        private string? ExtractBodyFromRawEmail(string contentType)
        {
            if (string.IsNullOrEmpty(RawEmail))
                return null;

            try
            {
                // Find the boundary
                var boundaryMatch = Regex.Match(RawEmail, @"boundary=""([^""]+)""");
                if (!boundaryMatch.Success)
                    return null;

                var boundary = boundaryMatch.Groups[1].Value;

                // Split by boundary
                var parts = RawEmail.Split(new[] { "--" + boundary }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var part in parts)
                {
                    if (part.Contains($"Content-Type: {contentType}", StringComparison.OrdinalIgnoreCase))
                    {
                        // Find the blank line that separates headers from body
                        var headerEndIndex = part.IndexOf("\r\n\r\n");
                        if (headerEndIndex < 0)
                            headerEndIndex = part.IndexOf("\n\n");

                        if (headerEndIndex > 0)
                        {
                            var headerSection = part.Substring(0, headerEndIndex);
                            var body = part.Substring(headerEndIndex).Trim();
                            
                            // Clean up boundary markers at the end
                            var endBoundary = body.IndexOf("--_");
                            if (endBoundary > 0)
                                body = body.Substring(0, endBoundary);

                            // Handle different transfer encodings
                            if (headerSection.Contains("base64", StringComparison.OrdinalIgnoreCase))
                            {
                                body = DecodeBase64(body);
                            }
                            else if (headerSection.Contains("quoted-printable", StringComparison.OrdinalIgnoreCase))
                            {
                                body = DecodeQuotedPrintable(body);
                            }

                            return body.Trim();
                        }
                    }
                }
            }
            catch
            {
                // If parsing fails, return null
            }

            return null;
        }

        /// <summary>
        /// Decodes base64 encoded text.
        /// </summary>
        private static string DecodeBase64(string input)
        {
            try
            {
                // Remove whitespace/newlines that may be in the base64 string
                var cleanBase64 = Regex.Replace(input, @"\s+", "");
                var bytes = Convert.FromBase64String(cleanBase64);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // If base64 decode fails, return the original
                return input;
            }
        }

        /// <summary>
        /// Decodes quoted-printable encoded text.
        /// </summary>
        private static string DecodeQuotedPrintable(string input)
        {
            // Handle soft line breaks (=\r\n or =\n)
            input = Regex.Replace(input, @"=\r?\n", "");

            // Decode =XX hex sequences
            return Regex.Replace(input, @"=([0-9A-Fa-f]{2})", match =>
            {
                var hex = match.Groups[1].Value;
                var charCode = Convert.ToInt32(hex, 16);
                return ((char)charCode).ToString();
            });
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
