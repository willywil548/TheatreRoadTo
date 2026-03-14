using System.Text.RegularExpressions;

namespace Theatre_TimeLine.Services
{
    internal static class YouTubeUrlParser
    {
        private static readonly Regex IframeSrcRegex = new("src\\s*=\\s*['\"](?<src>[^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VideoIdRegex = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
        private static readonly Regex PathIdRegex = new(@"(?:^|/)(?:embed|v|shorts|live)/(?<id>[A-Za-z0-9_-]{11})(?:$|[?&/#])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ShortUrlRegex = new(@"(?:^|/)youtu\.be/(?<id>[A-Za-z0-9_-]{11})(?:$|[?&/#])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string? ExtractVideoId(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var candidate = input.Trim();

            // If iframe HTML was pasted, extract src first.
            var srcMatch = IframeSrcRegex.Match(candidate);
            if (srcMatch.Success)
            {
                candidate = srcMatch.Groups["src"].Value;
            }

            // Bare ID.
            if (VideoIdRegex.IsMatch(candidate))
            {
                return candidate;
            }

            // Support URL without scheme.
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "https://" + candidate;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var host = uri.Host.ToLowerInvariant();
            if (!(host == "youtu.be"
                  || host == "youtube.com"
                  || host.EndsWith(".youtube.com")
                  || host == "youtube-nocookie.com"
                  || host.EndsWith(".youtube-nocookie.com")))
            {
                return null;
            }

            // watch?v=
            var query = uri.Query;
            if (!string.IsNullOrWhiteSpace(query))
            {
                foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && string.Equals(kv[0], "v", StringComparison.OrdinalIgnoreCase))
                    {
                        var id = Uri.UnescapeDataString(kv[1]);
                        if (VideoIdRegex.IsMatch(id))
                        {
                            return id;
                        }
                    }
                }
            }

            var absolute = uri.AbsoluteUri;

            // youtu.be/{id}
            var shortMatch = ShortUrlRegex.Match(absolute);
            if (shortMatch.Success)
            {
                return shortMatch.Groups["id"].Value;
            }

            // /embed/{id}, /shorts/{id}, /live/{id}, /v/{id}
            var pathMatch = PathIdRegex.Match(uri.AbsolutePath + uri.Query + uri.Fragment);
            if (pathMatch.Success)
            {
                return pathMatch.Groups["id"].Value;
            }

            return null;
        }
    }
}
