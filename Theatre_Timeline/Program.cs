using MudBlazor.Services;
using Theatre_Timeline.Contracts;
using Theatre_Timeline.Services;
using Theatre_TimeLine.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();

// Inject Settings
builder.Services.AddSingleton<IConfiguration>(builder.Configuration);

// Inject the tenant manager service.
builder.Services.AddSingleton<ITenantManagerService, TenantManagerService>();

// Inject the clipboard service.
builder.Services.AddScoped<IClipboardService, ClipboardService>();

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


app.Run();
