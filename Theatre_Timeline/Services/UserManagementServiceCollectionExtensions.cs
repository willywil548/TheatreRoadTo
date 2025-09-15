using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Theatre_TimeLine.Contracts;

namespace Theatre_TimeLine.Services
{
    public static class UserManagementServiceCollectionExtensions
    {
        public static IServiceCollection AddUserManagementServices(this IServiceCollection services, IConfiguration config)
        {
            // Use AzureAd first; keep Graph as optional fallback
            var tenantId = config["AzureAd:TenantId"] ?? config["AzureAd:Tenant"] ?? config["Graph:TenantId"];
            var clientId = config["AzureAd:ClientId"] ?? config["Graph:ClientId"];

            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
            {
                services.AddSingleton<ISecurityGroupService, StubSecurityGroupService>();
                return services;
            }

            // Load certificate from config/Key Vault-backed config
            var cert = LoadCertificate(config);
            TokenCredential? credential = null;

            if (cert != null)
            {
                var opts = new ClientCertificateCredentialOptions { SendCertificateChain = true };
                credential = new ClientCertificateCredential(tenantId!, clientId!, cert, opts);
            }
            else
            {
                // Optional fallback for dev/legacy
                var clientSecret = config["AzureAd:ClientSecret"] ?? config["Graph:ClientSecret"];
                if (!string.IsNullOrWhiteSpace(clientSecret))
                {
                    credential = new ClientSecretCredential(tenantId!, clientId!, clientSecret);
                }
            }

            if (credential == null)
            {
                services.AddSingleton<ISecurityGroupService, StubSecurityGroupService>();
                return services;
            }

            // Use app-only scope
            var graph = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
            services.AddSingleton(graph);
            services.AddSingleton<ISecurityGroupService, GraphSecurityGroupService>();
            return services;
        }

        private static X509Certificate2? LoadCertificate(IConfiguration config)
        {
            // Preferred: AzureAd:ClientCertificates base64/path/thumbprint
            var base64 = config["AzureAd:ClientCertificates:CertificateBase64"] ?? config["Graph:CertificateBase64"];
            var pwd = config["AzureAd:ClientCertificates:CertificatePassword"] ?? config["Graph:CertificatePassword"];
            if (!string.IsNullOrWhiteSpace(base64))
                return new X509Certificate2(Convert.FromBase64String(base64), pwd, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);

            var pfxPath = config["AzureAd:ClientCertificates:CertificatePath"] ?? config["Graph:CertificatePath"];
            if (!string.IsNullOrWhiteSpace(pfxPath) && File.Exists(pfxPath))
                return new X509Certificate2(pfxPath, pwd, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);

            var thumb = config["AzureAd:ClientCertificates:CertificateThumbprint"] ?? config["Graph:CertificateThumbprint"];
            if (!string.IsNullOrWhiteSpace(thumb))
            {
                var storeName = config["AzureAd:ClientCertificates:CertificateStoreName"] ?? config["Graph:CertificateStoreName"] ?? "My";
                thumb = thumb.Replace(" ", string.Empty).ToUpperInvariant();
                foreach (var loc in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
                {
                    using var store = new X509Store(storeName, loc);
                    store.Open(OpenFlags.ReadOnly);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false);
                    if (found.Count > 0) return found[0];
                }
            }

            // If a certificate name is provided, try to read the mapped KV secret value by that name
            var certName = config["AzureAd:ClientCertificates:CertificateName"];
            if (!string.IsNullOrWhiteSpace(certName))
            {
                var kvValue = config[certName]; // Key Vault provider maps secret by name
                if (!string.IsNullOrWhiteSpace(kvValue))
                {
                    try
                    {
                        return new X509Certificate2(Convert.FromBase64String(kvValue), pwd, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
                    }
                    catch { /* ignore if not base64 */ }
                }
            }

            return null;
        }
    }
}