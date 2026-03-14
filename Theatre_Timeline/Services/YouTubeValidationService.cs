using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Theatre_TimeLine.Services
{
    public interface IYouTubeValidationService
    {
        bool HasServerKey();
        Task<(bool ok, string? error)> ValidateVideoAsync(string videoId);
    }

    public class YouTubeValidationService : IYouTubeValidationService
    {
        private const string youTubeValidationFormat = "https://www.googleapis.com/youtube/v3/videos?part=status,contentDetails&id={0}&key={1}";
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<YouTubeValidationService> _logger;
        private readonly IMemoryCache _cache;
        private readonly string? _apiKey;

        public YouTubeValidationService(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<YouTubeValidationService> logger,
            IMemoryCache cache)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
            _cache = cache;
            _apiKey = _configuration["ApiKeys:YouTube"];
        }

        public bool HasServerKey()
        {
            return !string.IsNullOrWhiteSpace(_apiKey) && _apiKey != "<your-youtube-api-key>";
        }

        public async Task<(bool ok, string? error)> ValidateVideoAsync(string videoId)
        {
            if (!HasServerKey())
            {
                // Server YouTube API key not configured
                return (false, "Please contact the admin. Server is not setup correctly.");
            }

            // Use caching to avoid repeated hits for same video
            var cacheKey = $"yt_validate_{videoId}";
            if (_cache.TryGetValue<(bool, string?)>(cacheKey, out var cached))
            {
                return cached;
            }

            var safeVideoId = SanitizeForLog(videoId, out var htmlEncodedChanged);
            if (htmlEncodedChanged)
            {
                _logger.LogWarning("Potentially unsafe characters detected in video id input; HTML encoding changed the logged value. VideoId={VideoId}", safeVideoId);
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                var requestUri = string.Format(
                    youTubeValidationFormat,
                    Uri.EscapeDataString(videoId),
                    _apiKey);

                using var resp = await client.GetAsync(requestUri);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("YouTube API returned {Status} for video {VideoId}", resp.StatusCode, safeVideoId);
                    var result = (false, $"YouTube API request failed: {resp.StatusCode}");
                    _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                    return result;
                }

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;
                if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                {
                    var result = (false, "Video not found on YouTube.");
                    _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                    return result;
                }

                var item = items[0];

                // Check embeddable
                if (item.TryGetProperty("status", out var status))
                {
                    if (status.TryGetProperty("embeddable", out var emb) && emb.ValueKind == JsonValueKind.False)
                    {
                        var result = (false, "Video is not embeddable.");
                        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                        return result;
                    }
                }

                // Check age restriction
                if (item.TryGetProperty("contentDetails", out var contentDetails))
                {
                    if (contentDetails.TryGetProperty("contentRating", out var contentRating))
                    {
                        if (contentRating.TryGetProperty("ytRating", out var ytRating))
                        {
                            var val = ytRating.GetString();
                            if (!string.IsNullOrEmpty(val) && val.Equals("ytAgeRestricted", StringComparison.OrdinalIgnoreCase))
                            {
                                var result = (false, "Video is age-restricted and cannot be embedded.");
                                _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                                return result;
                            }
                        }
                    }
                }

                var okResult = (true, (string?)null);
                _cache.Set(cacheKey, okResult, TimeSpan.FromMinutes(30));
                return okResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating YouTube video {VideoId}", safeVideoId);
                var result = (false, "Error validating YouTube video");
                _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                return result;
            }
        }

        private static string SanitizeForLog(string? value, out bool htmlEncodedChanged)
        {
            htmlEncodedChanged = false;

            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(Math.Min(value.Length, 256));
            foreach (var ch in value)
            {
                // Remove non-printable ASCII control chars (U+0000..U+001F) plus DEL (U+007F).
                // This strips CR/LF/tab and similar characters that can break log line structure.
                if (ch < 0x20 || ch == 0x7F)
                {
                    continue;
                }

                sb.Append(ch);

                // Cap logged output length to keep logs bounded and readable.
                if (sb.Length >= 256)
                {
                    break;
                }
            }

            var sanitized = sb.ToString();
            if (sanitized.Length < value.Length)
            {
                sanitized += "...";
            }

            var encoded = WebUtility.HtmlEncode(sanitized);
            htmlEncodedChanged = !string.Equals(encoded, sanitized, StringComparison.Ordinal);
            return encoded;
        }
    }
}
