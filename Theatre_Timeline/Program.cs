using Cropper.Blazor.Extensions;
using MudBlazor;
using MudBlazor.Services;
using System.Diagnostics;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Services;

var builder = WebApplication.CreateBuilder(args);

Trace.WriteLine("Environment Variables:");
foreach (string var in System.Environment.GetEnvironmentVariables().Keys)
{
    Trace.WriteLine($"{var} = {System.Environment.GetEnvironmentVariable(var)}");
}

string webroot = builder.Environment.WebRootPath;
Trace.WriteLine($"ContentRoot Path: {builder.Environment.ContentRootPath}");
Trace.WriteLine($"WebRootPath: {webroot}");

builder.Configuration.AddEnvironmentVariables();
builder.Configuration["WebRootPath"] = webroot;

// Add services to the container.
builder.Services.AddRazorPages();

// Add server-side Blazor.
// Configure the default connection string for SignalR.
builder.Services.AddServerSideBlazor()
    .AddHubOptions(options =>
    {
        // Set the maximum message size to 32 MB.
        options.MaximumReceiveMessageSize = 32 * 1024 * 1000;
    });

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

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapControllers();

app.Run();
