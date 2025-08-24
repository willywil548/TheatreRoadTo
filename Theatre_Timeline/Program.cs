using Azure.Identity;
using Cropper.Blazor.Extensions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using MudBlazor;
using MudBlazor.Services;
using System.Diagnostics;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.AddEventSourceLogger(); // optional
builder.Logging.AddAzureWebAppDiagnostics(); // if deploying to Azure App Service

builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Theatre_TimeLine.Controllers.AuthenticationMetaDataController", LogLevel.Debug);

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
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUrl), credential);
}

// Add services to the container.
builder.Services.AddHttpContextAccessor()
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

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
        },
        // Uncomment temporarily to force a fresh sign-in (helps with stale AAD sessions)
        //OnRedirectToIdentityProvider = ctx =>
        //{
        //    ctx.ProtocolMessage.Prompt = "login";
        //    return Task.CompletedTask;
        //}
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

// Add server-side Blazor.
// Configure the default connection string for SignalR.
builder.Services.AddServerSideBlazor()
    .AddHubOptions(options =>
    {
        // Set the maximum message size to 32 MB.
        options.MaximumReceiveMessageSize = 32 * 1024 * 1000;
    })
    .AddMicrosoftIdentityConsentHandler();

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

app.MapGet("/.well-known/microsoft-identity-association.json", (ILoggerFactory lf, IConfiguration cfg) =>
{
    var log = lf.CreateLogger("WellKnown");
    var clientId = cfg["AzureAd:ClientId"];

    if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith("<your-clientid>", StringComparison.OrdinalIgnoreCase))
    {
        log.LogWarning("ClientId missing or placeholder in production config. Returning 404.");
        return Results.NotFound(new { error = "not-configured" });
    }

    log.LogInformation("Serving well-known for ClientId {ClientId}", SanitizeForLog(clientId));

    return Results.Json(new
    {
        associatedApplications = new[]
        {
            new { applicationId = clientId }
        }
    });
}).AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    // Diagnostic endpoint to confirm runtime view of config & FS
    var webRootPath = app.Environment.WebRootPath;
    app.MapGet("/diag/wellknown", (IConfiguration cfg) =>
    {
        var clientId = cfg["AzureAd:ClientId"] ?? "<null>";
        var fullPath = Path.Combine(webRootPath, ".well-known", "microsoft-identity-association.json");
        return Results.Ok(new
        {
            clientId,
            clientIdIsPlaceholder = clientId.StartsWith("<your-clientid>", StringComparison.OrdinalIgnoreCase),
            physicalFileExists = System.IO.File.Exists(fullPath),
            fullPath
        });
    }).AllowAnonymous();
}

app.MapControllers();

// Require auth for the Blazor Hub
app.MapBlazorHub().RequireAuthorization();

// Allow anonymous for the initial page request
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

// Helper to sanitize arbitrary strings for logging (removes control chars, truncates)
static string SanitizeForLog(string input)
{
    if (string.IsNullOrEmpty(input)) return string.Empty;
    Span<char> buffer = stackalloc char[input.Length];
    int idx = 0;
    foreach (var ch in input)
    {
        if (ch < 0x20) continue; // skip control chars (inc. CR/LF)
        buffer[idx++] = ch;
        if (idx >= 128) break; // limit length if needed
    }
    var sanitized = new string(buffer.Slice(0, idx));
    if (sanitized.Length < input.Length) sanitized += "...";
    return sanitized;
}
