using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Authentication scheme name and constants for token-based access.
    /// </summary>
    public static class TokenAuthenticationDefaults
    {
        /// <summary>
        /// The authentication scheme name used for token-based access.
        /// </summary>
        public const string AuthenticationScheme = "TokenAccess";

        /// <summary>
        /// The cookie name used to store the token ID.
        /// </summary>
        public const string CookieName = ".Theatre.TokenAccess";

        /// <summary>
        /// Default cookie expiration in days.
        /// </summary>
        public const int CookieExpirationDays = 30;
    }

    /// <summary>
    /// Options for token authentication.
    /// </summary>
    public class TokenAuthenticationOptions : AuthenticationSchemeOptions
    {
        /// <summary>
        /// Gets or sets the cookie expiration time. Default is 30 days.
        /// </summary>
        public TimeSpan CookieExpiration { get; set; } = TimeSpan.FromDays(TokenAuthenticationDefaults.CookieExpirationDays);
    }

    /// <summary>
    /// Custom authentication handler for token-based access.
    /// Reads the token ID from a cookie and validates it against stored tokens.
    /// </summary>
    public class TokenAuthenticationHandler : AuthenticationHandler<TokenAuthenticationOptions>
    {
        private readonly IAccessTokenService _tokenService;

        /// <summary>
        /// Initializes a new instance of the <see cref="TokenAuthenticationHandler"/> class.
        /// </summary>
        /// <param name="options">The options monitor for authentication options.</param>
        /// <param name="logger">The logger factory.</param>
        /// <param name="encoder">The URL encoder.</param>
        /// <param name="tokenService">The access token service for validating tokens.</param>
        public TokenAuthenticationHandler(
            IOptionsMonitor<TokenAuthenticationOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IAccessTokenService tokenService)
            : base(options, logger, encoder)
        {
            _tokenService = tokenService;
        }

        /// <summary>
        /// Handles authentication by reading the token cookie and validating the token.
        /// </summary>
        /// <returns>
        /// <see cref="AuthenticateResult.Success"/> if the token is valid;
        /// <see cref="AuthenticateResult.NoResult"/> if no token cookie is present;
        /// <see cref="AuthenticateResult.Fail"/> if the token is invalid or revoked.
        /// </returns>
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Check for token auth cookie
            if (!Request.Cookies.TryGetValue(TokenAuthenticationDefaults.CookieName, out var cookieValue))
            {
                return AuthenticateResult.NoResult();
            }

            if (string.IsNullOrEmpty(cookieValue))
            {
                return AuthenticateResult.Fail("Empty token cookie");
            }

            // Cookie contains the token ID
            if (!Guid.TryParse(cookieValue, out var tokenId))
            {
                return AuthenticateResult.Fail("Invalid token cookie format");
            }

            // Load and validate the token
            var token = await _tokenService.GetTokenByIdAsync(tokenId);
            if (token == null || !token.IsActive)
            {
                // Clear invalid cookie
                Response.Cookies.Delete(TokenAuthenticationDefaults.CookieName);
                return AuthenticateResult.Fail("Token not found or revoked");
            }

            // Build claims identity
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, token.TokenId.ToString()),
                new(ClaimTypes.Name, token.StudentName),
                new(ClaimTypes.Email, token.Email),
                new("token_id", token.TokenId.ToString()),
                new("tenant_id", token.TenantId.ToString()),
                new("auth_type", "token_access")
            };

            // Add road access claims
            foreach (var roadId in token.AuthorizedRoadIds)
            {
                claims.Add(new Claim("road_access", roadId.ToString()));
            }

            var identity = new ClaimsIdentity(claims, TokenAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, TokenAuthenticationDefaults.AuthenticationScheme);

            Logger.LogDebug("Token authentication successful for {StudentName}", token.StudentName);

            return AuthenticateResult.Success(ticket);
        }
    }

    /// <summary>
    /// Extension methods for token authentication.
    /// </summary>
    public static class TokenAuthenticationExtensions
    {
        /// <summary>
        /// Adds token-based authentication to the authentication builder.
        /// </summary>
        /// <param name="builder">The authentication builder.</param>
        /// <param name="configureOptions">Optional action to configure token authentication options.</param>
        /// <returns>The authentication builder for chaining.</returns>
        public static AuthenticationBuilder AddTokenAuthentication(
            this AuthenticationBuilder builder,
            Action<TokenAuthenticationOptions>? configureOptions = null)
        {
            return builder.AddScheme<TokenAuthenticationOptions, TokenAuthenticationHandler>(
                TokenAuthenticationDefaults.AuthenticationScheme,
                configureOptions ?? (_ => { }));
        }

        /// <summary>
        /// Maps the token authentication endpoints (set cookie, clear cookie).
        /// </summary>
        /// <param name="app">The web application.</param>
        /// <returns>The web application for chaining.</returns>
        public static WebApplication MapTokenAuthEndpoints(this WebApplication app)
        {
            // Endpoint to handle access links with plaintext token
            // This is the entry point for external users clicking their access link
            app.MapGet("/access/{token}", async (string token, IAccessTokenService tokenService, HttpContext ctx) =>
            {
                if (string.IsNullOrEmpty(token))
                {
                    return Results.Redirect("/access-denied.html?reason=No%20access%20token%20provided");
                }

                // Validate the plaintext token
                var validationResult = await tokenService.ValidateTokenAsync(token);

                if (!validationResult.IsValid || validationResult.Token == null)
                {
                    // Redirect to the static error page with the reason
                    var reason = Uri.EscapeDataString(validationResult.FailureReason ?? "Invalid or expired access link.");
                    return Results.Redirect($"/access-denied.html?reason={reason}");
                }

                var accessToken = validationResult.Token;

                // Set HttpOnly cookie with the token ID
                ctx.Response.Cookies.Append(
                    TokenAuthenticationDefaults.CookieName,
                    accessToken.TokenId.ToString(),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Lax,
                        Expires = DateTimeOffset.UtcNow.AddDays(TokenAuthenticationDefaults.CookieExpirationDays),
                        Path = "/"
                    });

                // Record the access
                await tokenService.RecordAccessAsync(accessToken.TokenId);

                // Determine redirect target
                var targetRoadId = accessToken.LastAccessedRoadId.HasValue &&
                                   accessToken.AuthorizedRoadIds.Contains(accessToken.LastAccessedRoadId.Value)
                    ? accessToken.LastAccessedRoadId.Value
                    : accessToken.AuthorizedRoadIds.FirstOrDefault();

                var redirectUrl = targetRoadId != Guid.Empty
                    ? $"/RoadToThere/{accessToken.TenantId}/{targetRoadId}"
                    : $"/RoadToThere/{accessToken.TenantId}";

                return Results.Redirect(redirectUrl);
            }).AllowAnonymous();

            // Endpoint to set the auth cookie by token ID (used internally after validation)
            app.MapGet("/api/auth/token/{tokenId}", async (Guid tokenId, IAccessTokenService tokenService, HttpContext ctx) =>
            {
                var token = await tokenService.GetTokenByIdAsync(tokenId);
                if (token == null || !token.IsActive)
                {
                    return Results.NotFound("Invalid or revoked token");
                }

                // Set HttpOnly cookie
                ctx.Response.Cookies.Append(
                    TokenAuthenticationDefaults.CookieName,
                    tokenId.ToString(),
                    new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Lax,
                        Expires = DateTimeOffset.UtcNow.AddDays(TokenAuthenticationDefaults.CookieExpirationDays),
                        Path = "/"
                    });

                // Record the access
                await tokenService.RecordAccessAsync(tokenId);

                // Determine redirect target
                var targetRoadId = token.LastAccessedRoadId.HasValue &&
                                   token.AuthorizedRoadIds.Contains(token.LastAccessedRoadId.Value)
                    ? token.LastAccessedRoadId.Value
                    : token.AuthorizedRoadIds.FirstOrDefault();

                var redirectUrl = targetRoadId != Guid.Empty
                    ? $"/RoadToThere/{token.TenantId}/{targetRoadId}"
                    : $"/RoadToThere/{token.TenantId}";

                return Results.Redirect(redirectUrl);
            }).AllowAnonymous();

            // Endpoint to clear the auth cookie (logout for token users)
            app.MapGet("/api/auth/token/logout", (HttpContext ctx, IConfiguration configuration) =>
            {
                ctx.Response.Cookies.Delete(TokenAuthenticationDefaults.CookieName, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/"
                });

                // Redirect to demo page (doesn't require authentication) instead of /home
                // Use configured demo tenant ID or fall back to a safe default
                var demoTenantId = configuration.GetValue<string>("TenantManager:DemoTenantId") 
                    ?? "00000000-0000-0000-0000-3eca75185852";
                return Results.Redirect($"/RoadToThere/{demoTenantId}");
            }).AllowAnonymous();

            return app;
        }

        /// <summary>
        /// Checks if the current user is authenticated via token access.
        /// </summary>
        /// <param name="user">The claims principal to check.</param>
        /// <returns><c>true</c> if authenticated via token; otherwise <c>false</c>.</returns>
        public static bool IsTokenAuthenticated(this ClaimsPrincipal user)
        {
            return user.Identity?.AuthenticationType == TokenAuthenticationDefaults.AuthenticationScheme;
        }

        /// <summary>
        /// Gets the token ID from the claims.
        /// </summary>
        /// <param name="user">The claims principal.</param>
        /// <returns>The token ID if present; otherwise <c>null</c>.</returns>
        public static Guid? GetTokenId(this ClaimsPrincipal user)
        {
            var claim = user.FindFirst("token_id");
            return claim != null && Guid.TryParse(claim.Value, out var id) ? id : null;
        }

        /// <summary>
        /// Gets the tenant ID from token claims.
        /// </summary>
        /// <param name="user">The claims principal.</param>
        /// <returns>The tenant ID if present; otherwise <c>null</c>.</returns>
        public static Guid? GetTokenTenantId(this ClaimsPrincipal user)
        {
            var claim = user.FindFirst("tenant_id");
            return claim != null && Guid.TryParse(claim.Value, out var id) ? id : null;
        }

        /// <summary>
        /// Gets the authorized road IDs from the claims.
        /// </summary>
        /// <param name="user">The claims principal.</param>
        /// <returns>An enumerable of authorized road IDs.</returns>
        public static IEnumerable<Guid> GetAuthorizedRoadIds(this ClaimsPrincipal user)
        {
            return user.FindAll("road_access")
                .Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty);
        }

        /// <summary>
        /// Checks if the user has access to a specific road.
        /// </summary>
        /// <param name="user">The claims principal.</param>
        /// <param name="roadId">The road ID to check access for.</param>
        /// <returns><c>true</c> if the user has access; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// AAD-authenticated users are assumed to have access (handled by SecurityGroupService).
        /// Token-authenticated users are checked against their road_access claims.
        /// </remarks>
        public static bool HasRoadAccess(this ClaimsPrincipal user, Guid roadId)
        {
            // AAD-authenticated users with proper permissions have full access
            if (!user.IsTokenAuthenticated())
            {
                return true; // Let the existing security service handle AAD users
            }

            return user.GetAuthorizedRoadIds().Contains(roadId);
        }
    }
}
