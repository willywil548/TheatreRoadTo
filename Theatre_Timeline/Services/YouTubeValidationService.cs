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
                    _logger.LogWarning("YouTube API returned {Status} for video {VideoId}", resp.StatusCode, videoId);
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
                _logger.LogError(ex, "Error validating YouTube video {VideoId}", videoId);
                var result = (false, "Error validating YouTube video");
                _cache.Set(cacheKey, result, TimeSpan.FromMinutes(5));
                return result;
            }
        }
    }
}
