using System.Diagnostics;
using System.Text.Json;
using System.Globalization;
using Theatre_TimeLine.Contracts;
using Theatre_TimeLine.Models;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// This class handles tenant management and storage of information related to the tenant.
    /// </summary>
    internal sealed class TenantManagerService : ITenantManagerService
    {
        /// <summary>
        /// The default GUID used for the demo tenant if not configured.
        /// </summary>
        public const string DefaultDemoGuid = "00000000-0000-0000-0000-3eca75185852";

        /// <summary>
        /// Gets the configured demo tenant GUID.
        /// </summary>
        public string DemoTenantId { get; }

        private const string HomeVariable = "%home%";
        private const string HomeVariableUnix = "$home";
        private const string HomeVariableUnixBraced = "${home}";
        private static readonly SemaphoreSlim writeManager = new(1, 1);
        private const string dataPathConfigKey = "TenantManager:DataPath";
        private const string demoTenantIdConfigKey = "TenantManager:DemoTenantId";
        private const string tenantConfigurationFile = "TenantConfiguration.json";
        private readonly string dataPath;
        private readonly ISecurityGroupService? _securityGroups;
        private readonly string[] _demoYouTubeLinks;

        // Marker file used to track when demo was last generated
        private readonly string demoMarkerFileName = "_demo_last_reset.txt";

        /// <summary>
        /// Initializes a new instance of <see cref="TenantManagerService"/>.
        /// </summary>
        /// <param name="configuration">The application configuration.</param>
        /// <param name="securityGroups">Optional: security group service to ensure groups upon tenant/road creation.</param>
        public TenantManagerService(IConfiguration configuration, ISecurityGroupService? securityGroups = null)
        {
            this._securityGroups = securityGroups;
            this._demoYouTubeLinks = LoadDemoYouTubeLinks(configuration);

            // Read demo tenant ID from configuration or use default
            this.DemoTenantId = configuration.GetValue<string>(demoTenantIdConfigKey) ?? DefaultDemoGuid;

            string? dataPath = configuration.GetValue<string>(dataPathConfigKey);
            LogInfo($"Configured data path value: '{dataPath ?? "<null>"}'");
            if (string.IsNullOrEmpty(dataPath))
            {
                // Default to HOME-based storage to preserve data across App Service publishes.
                dataPath = "%home%/webapps/data";
                LogInfo($"Data path config missing; defaulting to '{dataPath}'.");
            }

            dataPath = ResolveHomePathPrefix(dataPath);
            LogInfo($"Data path after HOME token resolution: '{dataPath}'");

            if (!Path.IsPathRooted(dataPath))
            {
                // Relative paths resolve under app binaries/content, which can be replaced on publish.
                LogInfo($"Data path is still relative; resolving against AppDomain base path '{AppDomain.CurrentDomain.BaseDirectory}'.");
                dataPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    dataPath.Trim(new[] { '.', '\\', '/' }));
            }

            this.dataPath = dataPath;
            LogInfo($"Final tenant data path: '{this.dataPath}'");

            // Ensure base data path exists
            Directory.CreateDirectory(this.dataPath);

            // Ensure demo tenant exists and is fresh
            EnsureDemoDataFresh();

            // Ensure existing demo video addresses have valid YouTube URLs from configuration
            EnsureDemoVideoLinksApplied();
        }

        /// <summary>
        /// Resolves configured path values that begin with a home-directory token
        /// into a concrete absolute path for both Windows and Linux hosting.
        /// </summary>
        /// <param name="configuredPath">Configured path value.</param>
        /// <returns>The resolved path value.</returns>
        private static string ResolveHomePathPrefix(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return configuredPath;
            }

            // Support legacy token (%home%) and Unix-friendly tokens ($home, ${home}, ~).
            int homeTokenLength = configuredPath.StartsWith(HomeVariable, StringComparison.OrdinalIgnoreCase)
                ? HomeVariable.Length
                : configuredPath.StartsWith(HomeVariableUnixBraced, StringComparison.OrdinalIgnoreCase)
                    ? HomeVariableUnixBraced.Length
                    : configuredPath.StartsWith(HomeVariableUnix, StringComparison.OrdinalIgnoreCase)
                        ? HomeVariableUnix.Length
                        : configuredPath.StartsWith("~", StringComparison.Ordinal)
                            ? 1
                            : 0;

            if (homeTokenLength == 0)
            {
                return configuredPath;
            }

            string home = GetHomeDirectory();
            if (string.IsNullOrWhiteSpace(home))
            {
                LogInfo($"Detected HOME token in data path '{configuredPath}', but no HOME/USERPROFILE env var was available.");
                return configuredPath;
            }

            string relativePath = configuredPath[homeTokenLength..].Trim(['\\', '/']);
            string rootHomePath = Path.GetFullPath(home);

            // Return HOME itself when only the token is provided.
            return string.IsNullOrWhiteSpace(relativePath)
                ? rootHomePath
                : Path.Combine(rootHomePath, relativePath);
        }

        /// <summary>
        /// Writes startup diagnostics to output streams visible in local and App Service logs.
        /// </summary>
        /// <param name="message">The message to write.</param>
        private static void LogInfo(string message)
        {
            var formattedMessage = $"[TenantManagerService] {message}";

            // Console output is surfaced by Azure App Service log streaming when enabled.
            Console.WriteLine(formattedMessage);

            // Trace output supports local diagnostics and any configured trace listeners.
            Trace.WriteLine(formattedMessage);
        }

        /// <summary>
        /// Gets the current process home directory in a cross-platform-safe way.
        /// </summary>
        /// <returns>The discovered home directory path, or an empty string if unavailable.</returns>
        private static string GetHomeDirectory()
        {
            // Linux/macOS generally expose HOME; Windows commonly exposes USERPROFILE.
            string? home = Environment.GetEnvironmentVariable("HOME")
                ?? Environment.GetEnvironmentVariable("home")
                ?? Environment.GetEnvironmentVariable("USERPROFILE");

            if (!string.IsNullOrWhiteSpace(home))
            {
                return home;
            }

            // Last-resort Windows fallback.
            string? homeDrive = Environment.GetEnvironmentVariable("HOMEDRIVE");
            string? homePath = Environment.GetEnvironmentVariable("HOMEPATH");
            if (!string.IsNullOrWhiteSpace(homeDrive) && !string.IsNullOrWhiteSpace(homePath))
            {
                return string.Concat(homeDrive, homePath);
            }

            return string.Empty;
        }

        /// <inheritdoc />
        public void CreateTenant(ITenantContainer tenant)
        {
            writeManager.Wait();
            try
            {
                FileInfo tenantConfigurationFileInfo = new(
                    Path.Combine(
                        this.GetTenantRootPath(tenant.TenantId),
                        tenantConfigurationFile));
                if (tenantConfigurationFileInfo.Exists)
                {
                    tenantConfigurationFileInfo.Delete();
                }

                tenantConfigurationFileInfo.Directory?.Create();
                string tenantConfig = JsonSerializer.Serialize(tenant);
                File.WriteAllText(tenantConfigurationFileInfo.FullName, tenantConfig);

                // Optionally ensure tenant-level groups.
                if (this._securityGroups != null)
                {
                    _ = this._securityGroups.EnsureGroupAsync(SecurityGroupNameBuilder.TenantManager(tenant.TenantId));
                    _ = this._securityGroups.EnsureGroupAsync(SecurityGroupNameBuilder.TenantUser(tenant.TenantId));
                }
            }
            finally
            {
                writeManager.Release();
            }
        }

        /// <inheritdoc />
        public void RemoveTenant(Guid guid)
        {
            writeManager.Wait();
            try
            {
                DirectoryInfo tenantDirectory = new(this.GetTenantRootPath(guid));
                if (tenantDirectory.Exists)
                {
                    tenantDirectory.Delete(recursive: true);
                }
            }
            finally
            {
                writeManager.Release();
            }
        }

        /// <inheritdoc />
        public void SaveRoad(IRoadToThere? roadToThere)
        {
            if (roadToThere == null)
            {
                return;
            }

            this.ActionRoad(roadToThere.TenantId, tenant => tenant.SaveRoad(roadToThere));

            // Optionally ensure road-level group.
            if (this._securityGroups != null)
            {
                _ = this._securityGroups.EnsureGroupAsync(SecurityGroupNameBuilder.TenantRoadUser(roadToThere.TenantId, roadToThere.RoadId));
            }
        }

        /// <inheritdoc />
        public void RemoveRoad(Guid roadId)
        {
            this.ActionRoad(roadId, tenant => tenant.RemoveRoad(roadId));
        }

        /// <inheritdoc />
        public bool TryAppendAddressToRoad(Guid tenantId, Guid roadId, Address address)
        {
            writeManager.Wait();
            try
            {
                var tenant = this.GetTenant(tenantId);
                if (tenant == null)
                {
                    return false;
                }

                var road = tenant.Roads.FirstOrDefault(r => r.RoadId == roadId);
                if (road == null || road.TenantId != tenantId)
                {
                    return false;
                }

                var existingAddresses = road.Addresses ?? [];
                road.Addresses = [.. existingAddresses, address];
                tenant.SaveRoad(road);
                return true;
            }
            finally
            {
                writeManager.Release();
            }
        }

        /// <inheritdoc />
        public IRoadToThere GetRoad(Guid roadId)
        {
            ITenantContainer? tenant = this.GetTenant(roadId);
            if (tenant == null)
            {
                throw new InvalidOperationException("Tenant not found.");
            }

            return tenant.Roads.FirstOrDefault(r => r.RoadId.Equals(roadId))
                ?? throw new InvalidOperationException("Road not found.");
        }

        /// <inheritdoc />
        public ITenantContainer? GetTenant(Guid guid)
        {
            ITenantContainer[] tenantContainers = this.GetTenants();
            return tenantContainers.FirstOrDefault(c => c.TenantId.Equals(guid))
                ?? tenantContainers.FirstOrDefault(c => c.Roads.Any(r => r.RoadId.Equals(guid)));
        }

        /// <inheritdoc />
        public ITenantContainer[] GetTenants()
        {
            List<ITenantContainer> containers = [];

            // Get the web apps by folder.
            foreach (string dir in Directory.GetDirectories(this.dataPath, "*", SearchOption.TopDirectoryOnly))
            {
                FileInfo fileInfo = new FileInfo(Path.Combine(dir, tenantConfigurationFile));
                if (!fileInfo.Exists)
                {
                    continue;
                }

                // Gets the basic information about Tenants.
                try
                {
                    TenantContainer? tenant = JsonSerializer.Deserialize<TenantContainer>(File.ReadAllText(fileInfo.FullName));
                    if (tenant != null)
                    {
                        tenant.TenantPath = fileInfo.Directory?.FullName;
                        containers.Add(tenant);
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex.Message);
                }
            }

            return [.. containers];
        }

        /// <summary>
        /// Performs an action on a road within a tenant context with thread safety.
        /// </summary>
        /// <param name="tenantId">The tenant ID.</param>
        /// <param name="action">The action to perform on the tenant container.</param>
        private void ActionRoad(Guid tenantId, Action<ITenantContainer> action)
        {
            writeManager.Wait();
            try
            {
                ITenantContainer? tenant = this.GetTenant(tenantId);
                if (tenant == null)
                {
                    return;
                }

                action?.Invoke(tenant);
            }
            finally
            {
                writeManager.Release();
            }
        }

        /// <summary>
        /// Ensure demo data exists and is fresh (resets nightly).
        /// </summary>
        private void EnsureDemoDataFresh()
        {
            try
            {
                var markerPath = Path.Combine(this.dataPath, demoMarkerFileName);
                var todayUtc = DateTime.UtcNow.Date;

                // If demo tenant folder doesn't exist, create demo now
                var demoRoot = Path.Combine(this.dataPath, DemoTenantId);
                if (!Directory.Exists(demoRoot))
                {
                    CreateDemoPage();
                    File.WriteAllText(markerPath, todayUtc.ToString("yyyy-MM-dd"));
                    return;
                }

                // If marker file missing or older than today, reset demo
                if (!File.Exists(markerPath))
                {
                    // reset
                    if (Directory.Exists(demoRoot))
                        Directory.Delete(demoRoot, recursive: true);

                    CreateDemoPage();
                    File.WriteAllText(markerPath, todayUtc.ToString("yyyy-MM-dd"));
                    return;
                }

                var markerText = File.ReadAllText(markerPath).Trim();
                if (!DateTime.TryParseExact(markerText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastResetDate))
                {
                    // Invalid marker - reset
                    if (Directory.Exists(demoRoot))
                        Directory.Delete(demoRoot, recursive: true);

                    CreateDemoPage();
                    File.WriteAllText(markerPath, todayUtc.ToString("yyyy-MM-dd"));
                    return;
                }

                if (lastResetDate.Date < todayUtc)
                {
                    // Older than today - reset demo
                    if (Directory.Exists(demoRoot))
                        Directory.Delete(demoRoot, recursive: true);

                    CreateDemoPage();
                    File.WriteAllText(markerPath, todayUtc.ToString("yyyy-MM-dd"));
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to ensure demo data freshness: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates existing demo video addresses so they use configured YouTube links when invalid content is present.
        /// This avoids waiting for the next scheduled demo reset.
        /// </summary>
        private void EnsureDemoVideoLinksApplied()
        {
            try
            {
                if (!Guid.TryParse(DemoTenantId, out var demoTenantGuid))
                {
                    return;
                }

                var tenant = GetTenant(demoTenantGuid);
                if (tenant == null || tenant.Roads == null || tenant.Roads.Length == 0)
                {
                    return;
                }

                foreach (var road in tenant.Roads)
                {
                    if (road?.Addresses == null || road.Addresses.Length == 0)
                    {
                        continue;
                    }

                    bool roadChanged = false;
                    int videoLinkIndex = 0;

                    foreach (var address in road.Addresses.Where(a => a.AddressType == AddressType.Video))
                    {
                        bool hasValidYouTubeId = !string.IsNullOrWhiteSpace(YouTubeUrlParser.ExtractVideoId(address.Content));
                        if (!hasValidYouTubeId)
                        {
                            address.Content = _demoYouTubeLinks[videoLinkIndex % _demoYouTubeLinks.Length];
                            roadChanged = true;
                        }

                        videoLinkIndex++;
                    }

                    if (roadChanged)
                    {
                        SaveRoad(road);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to apply demo video links: {ex.Message}");
            }
        }

        private static string[] LoadDemoYouTubeLinks(IConfiguration configuration)
        {
            var links = configuration
                .GetSection("TenantManager:DemoVideoLinks")
                .GetChildren()
                .Select(x => x.GetSection("VideoLink:Location").Value)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .ToArray();

            if (links.Length > 0)
            {
                return links;
            }

            return
            [
                "https://www.youtube.com/watch?v=c51ND9Hdbw0",
                "https://www.youtube.com/embed/hJnAHzo4-KI"
            ];
        }

        /// <summary>
         /// Creates the demo page and tenant for first-time setup.
         /// </summary>
        private void CreateDemoPage()
        {
            // Setup the Demo tenant container.
            ITenantContainer tenant = new TenantContainer
            {
                TenantName = "Demo",
                Description = "Demo Road to highlight some capabilities.",
                AdminSecurityGroup = "Demo-RoadToThere",
                TenantId = Guid.Parse(DemoTenantId),
                TenantPath = Path.Combine(this.dataPath, DemoTenantId)
            };

            // Persist tenant configuration
            this.CreateTenant(tenant);

            // Build a demo road with multiple addresses
            var now = DateTime.Now;
            var road = new RoadToThere
            {
                RoadId = Guid.Parse(DemoTenantId),
                Description = "Road showcasing features from start to finish",
                TenantId = tenant.TenantId,
                StartTime = now.AddDays(-2), // demo starts T-2 days
                EndTime = now.AddDays(-2).AddDays(10), // run for 10 days total
                Title = "Full Demo of capabilities",
                RoadAdmin = tenant.AdminSecurityGroup,
            };

            // Generate ~10 addresses across the road range
            var addresses = new List<Address>();
            int count = 10;
            var span = (road.EndTime!.Value - road.StartTime!.Value).TotalMinutes;
            for (int i = 0; i < count; i++)
            {
                var offset = TimeSpan.FromMinutes((span * i) / Math.Max(1, count - 1));
                var loc = road.StartTime.Value.Add(offset);

                // Alternate types: Notification, Survey, Video
                var mod = i % 3;
                if (mod == 1)
                {
                    // Poll
                    var poll = new Poll
                    {
                        PollId = Guid.NewGuid(),
                        Question = i == 1 ? "Will you attend the event?" : $"Question {i}",
                        PollType = (i % 2 == 0) ? PollType.MultipleChoice : PollType.YesNo,
                        Options = (i % 2 == 0) ? new List<string> { "Option A", "Option B", "Option C" } : new List<string> { "Yes", "No" }
                    };

                    var pollAddr = new PollAddress
                    {
                        Title = i == 1 ? "Survey: Attendance" : $"Survey {i}",
                        Description = "Please participate in this quick poll.",
                        Location = loc,
                        DelayRelease = false
                    };

                    // Use extension to set poll JSON content
                    pollAddr.SetPoll(poll);
                    addresses.Add(pollAddr);
                }
                else
                {
                    if (mod == 0)
                    {
                        var addr = new Address
                        {
                            Title = i == 0 ? "Welcome to the Demo" : $"Update {i}",
                            Description = "Demo content showing timeline events.",
                            Content = i % 2 == 0 ? "Short announcement content." : "Additional details about this event.",
                            Location = loc,
                            DelayRelease = false,
                            AddressType = AddressType.Notification
                        };

                        addresses.Add(addr);
                    }
                    else
                    {
                        var videoLink = _demoYouTubeLinks[(i / 3) % _demoYouTubeLinks.Length];
                        var addr = new Address
                        {
                            Title = $"Update {i}",
                            Description = "Video update for the timeline.",
                            Content = videoLink,
                            Location = loc,
                            DelayRelease = false,
                            AddressType = AddressType.Video
                        };

                        addresses.Add(addr);
                    }
                }
            }

            road.Addresses = addresses.ToArray();

            // Save the road
            this.SaveRoad(road);
        }

        /// <inheritdoc />
        public string GetTenantRootPath(Guid tenantId)
        {
            return Path.Combine(this.dataPath, tenantId.ToString());
        }
    }
}