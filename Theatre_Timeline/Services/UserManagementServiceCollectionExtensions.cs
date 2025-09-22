using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
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
            bool hasSecretService = services.Any(serviceDescriptor => serviceDescriptor.ServiceType == typeof(SecretClient));

            if (hasSecretService)
            {
                services.AddSingleton(sp =>
                {
                    var config = sp.GetRequiredService<IConfiguration>();
                    var secretClient = sp.GetRequiredService<SecretClient>();

                    var tenantId = config["AzureAd:TenantId"];
                    var clientId = config["AzureAd:ClientId"];
                    var certSecretName = config["AzureAd:ClientCertificates:CertificateName"];

                    // Fetch PFX from Key Vault as base64-encoded secret
                    KeyVaultSecret secret = secretClient.GetSecret(certSecretName).Value;
                    var certBytes = Convert.FromBase64String(secret.Value);

                    // Load certificate safely in App Service
                    var cert = new X509Certificate2(certBytes, string.Empty, X509KeyStorageFlags.EphemeralKeySet);

                    var credential = new ClientCertificateCredential(tenantId, clientId, cert);
                    return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
                });
                services.AddSingleton<ISecurityGroupService, GraphSecurityGroupService>();
                services.AddHostedService<GraphSecurityGroupService>(p => (GraphSecurityGroupService)p.GetRequiredService<ISecurityGroupService>());
            }
            else
            {
                services.AddSingleton<ISecurityGroupService, StubSecurityGroupService>();
            }

            return services;
        }
    }
}