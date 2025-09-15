using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// DI extensions to register user-management services.
    /// Builds a Microsoft Graph client using the configured Azure AD app
    /// (preferring client certificate auth via AzureAd:ClientCertificates) and
    /// registers the <see cref="ISecurityGroupService"/> implementation.
    /// Falls back to a stub when Graph is not configured.
    /// </summary>
    public static class UserManagementServiceCollectionExtensions
    {
        /// <summary>
        /// Registers Graph or Stub implementations for user/group management based on configuration.
        /// Expects AzureAd:TenantId and AzureAd:ClientId; prefers a client certificate under AzureAd:ClientCertificates.
        /// </summary>
        public static IServiceCollection AddUserManagementServices(this IServiceCollection services, IConfiguration config)
        {
            // Prefer AzureAd config (system account already wired in Program.cs), fallback to Graph keys
            var tenantId = config["AzureAd:TenantId"] ?? config["Graph:TenantId"];
            var clientId = config["AzureAd:ClientId"] ?? config["Graph:ClientId"];

            TokenCredential? credential = null;

            if (!string.IsNullOrWhiteSpace(tenantId) && !string.IsNullOrWhiteSpace(clientId))
            {
                // Prefer client certificate (from Key Vault-backed config or other sources)
                var cert = LoadCertificateFromConfig(config, "AzureAd:ClientCertificates") ?? LoadCertificateFromConfig(config, "Graph");
                if (cert != null)
                {
                    var options = new ClientCertificateCredentialOptions { SendCertificateChain = true };
                    credential = new ClientCertificateCredential(tenantId!, clientId!, cert, options);
                }
                else
                {
                    var clientSecret = config["AzureAd:ClientSecret"] ?? config["Graph:ClientSecret"];
                    if (!string.IsNullOrWhiteSpace(clientSecret))
                    {
                        credential = new ClientSecretCredential(tenantId!, clientId!, clientSecret);
                    }
                }
            }

            if (credential != null)
            {
                var graph = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
                services.AddSingleton(graph);
                services.AddSingleton<ISecurityGroupService, GraphSecurityGroupService>();
            }
            else
            {
                services.AddSingleton<ISecurityGroupService, StubSecurityGroupService>();
            }

            return services;
        }

        /// <summary>
        /// Attempts to load a client certificate from configuration (base64 PFX, PFX path, or store thumbprint).
        /// </summary>
        private static X509Certificate2? LoadCertificateFromConfig(IConfiguration config, string prefix)
        {
            var base64 = config[$"{prefix}:CertificateBase64"];
            var base64Pwd = config[$"{prefix}:CertificatePassword"];
            if (!string.IsNullOrWhiteSpace(base64))
            {
                var raw = Convert.FromBase64String(base64);
                return new X509Certificate2(raw, base64Pwd, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
            }

            var pfxPath = config[$"{prefix}:CertificatePath"];
            var pfxPwd = config[$"{prefix}:CertificatePassword"];
            if (!string.IsNullOrWhiteSpace(pfxPath) && File.Exists(pfxPath))
            {
                return new X509Certificate2(pfxPath, pfxPwd, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
            }

            var thumb = config[$"{prefix}:CertificateThumbprint"];
            if (!string.IsNullOrWhiteSpace(thumb))
            {
                thumb = thumb.Replace(" ", string.Empty).ToUpperInvariant();
                var storeName = config[$"{prefix}:CertificateStoreName"] ?? "My";

                foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                {
                    using var store = new X509Store(storeName, location);
                    try
                    {
                        store.Open(OpenFlags.ReadOnly);
                        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false);
                        if (found.Count > 0) return found[0];
                    }
                    finally
                    {
                        store.Close();
                    }
                }
            }

            return null;
        }
    }
}