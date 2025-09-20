using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using System.Security.Cryptography.X509Certificates;
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
            string? certificateName = config.GetValue<string>($"{prefix}:CertificateName");
            if (string.IsNullOrEmpty(certificateName))
            {
                return null;
            }

            string? base64 = config.GetValue<string>(certificateName);
            if (string.IsNullOrEmpty(base64))
            {
                return null;
            }

            // Use the Azure Key Vault Cert.
            var raw = Convert.FromBase64String(base64);
            return new X509Certificate2(raw);
        }
    }
}