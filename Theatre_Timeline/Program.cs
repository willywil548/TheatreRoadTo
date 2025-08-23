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

app.MapControllers();

// Require auth for the Blazor Hub
app.MapBlazorHub().RequireAuthorization();

// Allow anonymous for the initial page request
app.MapFallbackToPage("/_Host").AllowAnonymous();

app.MapGet("/.well-known/microsoft-identity-association.json", (IConfiguration cfg) =>
{
    var clientId = cfg["AzureAd:ClientId"];
    if (string.IsNullOrWhiteSpace(clientId))
    {
        // Log and return 500 with context
        return Results.Problem("AzureAd:ClientId missing");
    }

    return Results.Json(new
    {
        associatedApplications = new[]
        {
            new { applicationId = clientId }
        }
    });
})
.AllowAnonymous()
.Produces(StatusCodes.Status200OK, typeof(void), "application/json");

app.Run();
