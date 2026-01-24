using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Cropper.Blazor.Extensions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;  
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
    // Add services to the container with Azure AD authentication.
    builder.Services.AddHttpContextAccessor()
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(azureAdSection);

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
        // Require auth by default. Mark public pages/components with [AllowAnonymous].
        options.FallbackPolicy = options.DefaultPolicy;

        // Example policy based on app role
        options.AddPolicy("TenantAdminsOnly", policy =>
            policy.RequireClaim("roles", "Tenant.Admin"));
    });

    builder.Services.AddRazorPages()
        .AddMicrosoftIdentityUI();
}
else
{
    // No Azure AD configured - use basic authentication setup
    Trace.WriteLine("WARNING: Azure AD not configured. Running without authentication for testing purposes.");
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddAuthentication();
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

// Add email encryption service
builder.Services.AddSingleton<IEmailEncryptionService, EmailEncryptionService>();

// Add SendGrid webhook validator
builder.Services.AddSingleton<ISendGridWebhookValidator, SendGridWebhookValidator>();

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

// Log registered controllers for debugging
builder.Services.AddSingleton<ILogger>(sp => 
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Startup"));

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

// Redirect "/" to "/home" (302). Use permanent: true for 308.
app.MapGet("/", () => Results.Redirect("/home", permanent: false)).AllowAnonymous();

// Require auth for the Blazor Hub (only if Azure AD is configured)
if (hasAzureAdConfig)
{
    app.MapBlazorHub().RequireAuthorization();
}
else
{
    app.MapBlazorHub();
}

// Allow anonymous for the initial page request
// IMPORTANT: MapFallbackToPage must come AFTER MapControllers
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
