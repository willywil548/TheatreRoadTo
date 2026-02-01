using Microsoft.AspNetCore.DataProtection;
using System.Text;

namespace Theatre_TimeLine.Services
{
    /// <summary>
    /// Service for encrypting and decrypting email data using ASP.NET Core Data Protection API.
    /// </summary>
    public interface IEmailEncryptionService
    {
        /// <summary>
        /// Encrypts the provided plaintext data.
        /// </summary>
        /// <param name="plaintext">The data to encrypt.</param>
        /// <returns>Base64-encoded encrypted data.</returns>
        string Encrypt(string plaintext);

        /// <summary>
        /// Decrypts the provided encrypted data.
        /// </summary>
        /// <param name="encryptedData">Base64-encoded encrypted data.</param>
        /// <returns>The decrypted plaintext.</returns>
        string Decrypt(string encryptedData);

        /// <summary>
        /// Encrypts data and writes it to a file.
        /// </summary>
        /// <param name="filePath">The file path to write to.</param>
        /// <param name="data">The data to encrypt and write.</param>
        Task WriteEncryptedFileAsync(string filePath, string data);

        /// <summary>
        /// Reads and decrypts data from a file.
        /// </summary>
        /// <param name="filePath">The file path to read from.</param>
        /// <returns>The decrypted data.</returns>
        Task<string> ReadEncryptedFileAsync(string filePath);
    }

    /// <summary>
    /// Implementation of email encryption service using Data Protection API.
    /// </summary>
    public sealed class EmailEncryptionService : IEmailEncryptionService
    {
        private readonly IDataProtector _protector;
        private readonly ILogger<EmailEncryptionService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="EmailEncryptionService"/> class.
        /// </summary>
        /// <param name="dataProtectionProvider">The data protection provider.</param>
        /// <param name="logger">The logger instance.</param>
        public EmailEncryptionService(
            IDataProtectionProvider dataProtectionProvider,
            ILogger<EmailEncryptionService> logger)
        {
            // Create a protector with a specific purpose string
            _protector = dataProtectionProvider.CreateProtector("Theatre_TimeLine.EmailStorage.v1");
            _logger = logger;
        }

        /// <inheritdoc />
        public string Encrypt(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
            {
                return string.Empty;
            }

            try
            {
                var encryptedBytes = _protector.Protect(Encoding.UTF8.GetBytes(plaintext));
                return Convert.ToBase64String(encryptedBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to encrypt data");
                throw;
            }
        }

        /// <inheritdoc />
        public string Decrypt(string encryptedData)
        {
            if (string.IsNullOrEmpty(encryptedData))
            {
                return string.Empty;
            }

            try
            {
                var encryptedBytes = Convert.FromBase64String(encryptedData);
                var decryptedBytes = _protector.Unprotect(encryptedBytes);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt data");
                throw;
            }
        }

        /// <inheritdoc />
        public async Task WriteEncryptedFileAsync(string filePath, string data)
        {
            try
            {
                var encrypted = Encrypt(data);
                await File.WriteAllTextAsync(filePath, encrypted);
                _logger.LogDebug("Encrypted data written to {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write encrypted file to {FilePath}", filePath);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<string> ReadEncryptedFileAsync(string filePath)
        {
            try
            {
                var encryptedData = await File.ReadAllTextAsync(filePath);
                var decrypted = Decrypt(encryptedData);
                _logger.LogDebug("Decrypted data read from {FilePath}", filePath);
                return decrypted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read encrypted file from {FilePath}", filePath);
                throw;
            }
        }
    }
}