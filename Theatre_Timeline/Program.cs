using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Cropper.Blazor.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor;
using MudBlazor.Services;
using System.Diagnostics;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Services;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

var builder = WebApplication.CreateBuilder(args);

builder.Logging
    .AddConsole()
    .AddDebug()
    .AddEventSourceLogger()
    .AddAzureWebAppDiagnostics(); // if deploying to Azure App Service

builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

// Or global minimum (still overridden by specific category levels)
builder.Logging.SetMinimumLevel(LogLevel.Information);

string webroot = builder.Environment.WebRootPath;
Trace.WriteLine($"ContentRoot Path: {builder.Environment.ContentRootPath}");
Trace.WriteLine($"WebRootPath: {webroot}");

builder.Configuration.AddEnvironmentVariables();
builder.Configuration["WebRootPath"] = webroot;

bool useCert = builder.Configuration.GetValue<bool>("UseKeyVaultCert");

string? sourceType = builder.Configuration.GetValue<string>("AzureAd:ClientCertificates:SourceType");
string? certificateName = builder.Configuration.GetValue<string>("AzureAd:ClientCertificates:CertificateName");
string? keyVaultUrl = builder.Configuration.GetValue<string>("AzureAd:ClientCertificates:KeyVaultUrl");

if (useCert && !string.IsNullOrEmpty(keyVaultUrl))
{
    var credential = new DefaultAzureCredential();
    builder.Configuration["Graph:KeyVault"] = true.ToString();
    try
    {
        builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUrl), credential);
    }
    catch (Azure.RequestFailedException rfe) when (string.Equals("AKV10046", rfe.ErrorCode))
    {
        // Delay and retry
        await Task.Delay(TimeSpan.FromSeconds(10));
        builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUrl), credential);
    }

    builder.Services.AddSingleton(_ =>
        new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential()));
}

// Check if Azure AD is configured
var azureAdSection = builder.Configuration.GetSection("AzureAd");
bool hasAzureAdConfig = !string.IsNullOrEmpty(azureAdSection["Instance"]) &&
                        !string.IsNullOrEmpty(azureAdSection["TenantId"]);

if (hasAzureAdConfig)
{
    // Add services to the container with Azure AD + Token authentication.
    builder.Services.AddHttpContextAccessor();

    // Configure authentication with multi-scheme support
    var authBuilder = builder.Services.AddAuthentication(options =>
    {
        // Use a policy scheme to dynamically select the authentication scheme
        options.DefaultScheme = "MultiAuth";
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    });

    // Add Azure AD authentication
    authBuilder.AddMicrosoftIdentityWebApp(azureAdSection);

    // Add token-based auth for external users
    authBuilder.AddTokenAuthentication();

    // Add policy scheme to dynamically select between AAD and Token auth
    authBuilder.AddPolicyScheme("MultiAuth", "Azure AD or Token", options =>
    {
        // Dynamically select auth scheme based on request
        options.ForwardDefaultSelector = context =>
        {
            // If token cookie exists, use token authentication
            if (context.Request.Cookies.ContainsKey(TokenAuthenticationDefaults.CookieName))
            {
                return TokenAuthenticationDefaults.AuthenticationScheme;
            }

            // Otherwise use cookie auth (which is set up by Microsoft Identity)
            return CookieAuthenticationDefaults.AuthenticationScheme;
        };

        // Always challenge with OpenID Connect (Azure AD)
        options.ForwardChallenge = OpenIdConnectDefaults.AuthenticationScheme;
    });

    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        // Normalize claims so [Authorize(Roles="...")] and User.Identity.Name work as expected.
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.SaveTokens = true; // Useful if you later call downstream API

        options.Events = new OpenIdConnectEvents
        {
            OnRemoteFailure = ctx =>
            {
                // Logs the raw error; surface in Debug/Console as needed
                Debug.WriteLine($"OIDC RemoteFailure: {ctx.Failure?.Message}");
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                Debug.WriteLine($"OIDC AuthFailed: {ctx.Exception?.Message}");
                return Task.CompletedTask;
            }
        };
    });

    builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });

    builder.Services.AddAuthorization(options =>
    {
        // Don't use FallbackPolicy - it prevents anonymous access to pages that need it (like demo).
        // Instead, use [Authorize] attribute on pages that require authentication.
        // This allows the Blazor AuthorizeRouteView to handle authorization at the component level.

        // Example policy based on app role
        options.AddPolicy("TenantAdminsOnly", policy =>
            policy.RequireClaim("roles", "Tenant.Admin"));
    });

    builder.Services.AddRazorPages()
        .AddMicrosoftIdentityUI();
}
else
{
    // No Azure AD configured - use token authentication only
    Trace.WriteLine("WARNING: Azure AD not configured. Running with token authentication only.");
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddAuthentication(TokenAuthenticationDefaults.AuthenticationScheme)
        .AddTokenAuthentication();
    builder.Services.AddAuthorization();
    builder.Services.AddRazorPages();
}

// Add server-side Blazor.
// Configure the default connection string for SignalR.
var blazorBuilder = builder.Services.AddServerSideBlazor()
    .AddHubOptions(options =>
    {
        // Set the maximum message size to 32 MB.
        options.MaximumReceiveMessageSize = 32 * 1024 * 1000;
    });

if (hasAzureAdConfig)
{
    blazorBuilder.AddMicrosoftIdentityConsentHandler();
}

// Add MudBlazor services.
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
    config.SnackbarConfiguration.PreventDuplicates = true;
    config.SnackbarConfiguration.NewestOnTop = false;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 10000;
    config.SnackbarConfiguration.HideTransitionDuration = 500;
    config.SnackbarConfiguration.ShowTransitionDuration = 500;
    config.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
});

// Add Data Protection for encryption
builder.Services.AddDataProtection()
    .SetApplicationName("Theatre_TimeLine");

// Add access token service for external user authentication
builder.Services.AddSingleton<IAccessTokenService, AccessTokenService>();

// Add email encryption service
builder.Services.AddSingleton<IEmailEncryptionService, EmailEncryptionService>();

// Add SendGrid webhook validator
builder.Services.AddSingleton<ISendGridWebhookValidator, SendGridWebhookValidator>();
builder.Services.AddSingleton<InboundEmailProcessingQueue>();
builder.Services.AddSingleton<IInboundEmailProcessingQueue>(serviceProvider => serviceProvider.GetRequiredService<InboundEmailProcessingQueue>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<InboundEmailProcessingQueue>());
builder.Services.AddScoped<IInboundEmailProcessor, InboundEmailProcessor>();
builder.Services.AddSingleton<IInboundEmailAiExtractionService, AzureOpenAiInboundEmailAiExtractionService>();
builder.Services.AddSingleton<ISendGridEmailService, SendGridEmailService>();

// Add user management service (Graph if configured; stub otherwise)
builder.Services.AddUserManagementServices(builder.Configuration);

if (hasAzureAdConfig)
{
    builder.Services.AddHostedService<SecurityGroupStartupVerifier>();
}

// Add cropping services
builder.Services.AddCropper();

// Inject Settings
builder.Services.AddSingleton<IConfiguration>(builder.Configuration);

// Inject the tenant manager service.
builder.Services.AddSingleton<ITenantManagerService, TenantManagerService>();

// Inject the clipboard service.
builder.Services.AddScoped<IClipboardService, ClipboardService>();

// Add controllers
builder.Services.AddControllers();

// Add memory cache
builder.Services.AddMemoryCache();

// Register YouTube validation service
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IYouTubeValidationService, YouTubeValidationService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

var options = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
};
options.KnownNetworks.Clear();
options.KnownProxies.Clear();
app.UseForwardedHeaders(options);

app.UseAuthentication();
app.UseAuthorization();

// Diagnostic middleware: log ALL incoming requests
app.Use(async (ctx, next) =>
{
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("RequestDiagnostics");
    logger.LogInformation("Incoming request: {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
    await next();
    logger.LogInformation("Response status: {StatusCode}", ctx.Response.StatusCode);
});

// Diagnostic middleware: log every /.well-known request early
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/.well-known"))
    {
        var rawPath = ctx.Request.Path;
        var safePath = SanitizePath(rawPath);

        // Build a physical path candidate safely
        var relative = (rawPath.Value ?? string.Empty).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var physicalCandidate = Path.Combine(app.Environment.WebRootPath, relative);
        bool exists = File.Exists(physicalCandidate);

        var lf = ctx.RequestServices.GetRequiredService<ILoggerFactory>();
        lf.CreateLogger("WellKnownTrace")
          .LogInformation("Inbound .well-known request. PhysicalFileExists={PhysicalFileExists} Path={Path}", exists, safePath);
    }

    await next();
});

app.MapControllers();

// Map token authentication endpoints (set/clear cookie, and /access/{token} handler)
// IMPORTANT: This must come BEFORE MapFallbackToPage so /access/{token} is handled by the API
app.MapTokenAuthEndpoints();

// Redirect "/" to "/home" (302). Use permanent: true for 308.
app.MapGet("/", () => Results.Redirect("/home", permanent: false)).AllowAnonymous();

// Map Blazor Hub - allow anonymous connection, authorization is handled at page/component level
app.MapBlazorHub();

// Allow anonymous for the initial page request
// IMPORTANT: MapFallbackToPage must come AFTER MapControllers and MapTokenAuthEndpoints
// to ensure API routes are not captured by the Blazor fallback
app.MapFallbackToPage("/_Host").AllowAnonymous();

app.Run();

// Helper to sanitize user-provided path for logging (mitigates CodeQL log injection warning)
static string SanitizePath(PathString path)
{
    var value = path.Value ?? string.Empty;

    // Remove control chars (including CR/LF) and limit length
    Span<char> buffer = stackalloc char[value.Length];
    int idx = 0;
    foreach (var ch in value)
    {
        if (ch < 0x20) continue; // skip control chars
        buffer[idx++] = ch;
        if (idx >= 256) break;   // enforce max length
    }
    var sanitized = new string(buffer.Slice(0, idx));
    if (sanitized.Length < value.Length) sanitized += "...";
    return sanitized;
}
