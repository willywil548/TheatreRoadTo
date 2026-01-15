# SendGrid Email Inbound Parse - Implementation Summary

## Overview
Successfully implemented an encrypted email storage system for SendGrid's Inbound Parse webhook. Emails are received, encrypted using ASP.NET Core Data Protection API, and stored to disk for server-side processing.

## Components Created

### 1. Email Encryption Service
**File**: `Theatre_Timeline/Services/EmailEncryptionService.cs`

- Interface: `IEmailEncryptionService`
- Implementation: `EmailEncryptionService`
- Uses ASP.NET Core Data Protection API for encryption
- Provides methods for:
  - `Encrypt(string plaintext)` - Encrypts data to Base64
  - `Decrypt(string encryptedData)` - Decrypts from Base64
  - `WriteEncryptedFileAsync(filePath, data)` - Encrypts and writes to file
  - `ReadEncryptedFileAsync(filePath)` - Reads and decrypts from file

**Key Features**:
- Automatic key management via Data Protection API
- Purpose string: `"Theatre_TimeLine.EmailStorage.v1"`
- Keys stored in machine key ring (or Azure Key Vault if configured)

### 2. SendGrid Controller
**File**: `Theatre_Timeline/Controllers/SendGridController.cs`

**Endpoints**:

| Method | Endpoint | Purpose | Auth |
|--------|----------|---------|------|
| GET | `/api/sendgrid/health` | Health check and status | Anonymous |
| POST | `/api/sendgrid/inbound` | Receive email from SendGrid | Anonymous |

**Email Processing Flow**:
1. Receives multipart/form-data from SendGrid
2. Extracts all form fields (from, to, subject, text, html, headers, etc.)
3. Handles attachment metadata
4. Adds timestamp and encryption metadata
5. Serializes to JSON
6. Encrypts with Data Protection API
7. Saves as `.enc` file with naming pattern: `email_{timestamp}_{from}.enc`

**Security Features**:
- `[AllowAnonymous]` attribute (required for SendGrid webhooks)
- Encrypted storage - emails stored encrypted at rest
- No public retrieval endpoints - emails only accessible server-side
- Sanitized filenames
- App-relative paths in responses (not absolute paths)

### 3. Configuration
**File**: `Theatre_Timeline/appsettings.json`

```json
{
  "SendGrid": {
    "EmailStoragePath": "./emails",
    "EnableEncryption": true
  }
}
```

**Configurable Options**:
- `EmailStoragePath` - Where to store encrypted emails (default: `./emails`)
- `EnableEncryption` - Enable/disable encryption (default: `true`)

### 4. Program.cs Updates
**File**: `Theatre_Timeline/Program.cs`

**Added Services**:
```csharp
// Data Protection for encryption
builder.Services.AddDataProtection()
    .SetApplicationName("Theatre_TimeLine");

// Email encryption service
builder.Services.AddSingleton<IEmailEncryptionService, EmailEncryptionService>();
```

**Added Middleware**:
- Request diagnostics logging (logs all incoming requests)
- Controllers mapped with `app.MapControllers()`

**Azure AD Optional**:
- Made Azure AD authentication optional for testing
- Falls back to basic auth if Azure AD not configured

## Test Scripts

### PowerShell Tests
1. **test-sendgrid-https.ps1** - Basic health check endpoint testing
2. **test-post-email.ps1** - Full email POST test with encryption verification

### Bash Tests
1. **test-sendgrid.sh** - Basic health check endpoint testing for Linux/Mac
2. **test-post-email.sh** - Full email POST test for Linux/Mac

## SendGrid Configuration

### Inbound Parse Setup
1. Go to SendGrid Dashboard ? Settings ? Inbound Parse
2. Add destination URL: `https://yourdomain.com/api/sendgrid/inbound`
3. Configure domain/subdomain to forward emails
4. SendGrid will POST to your endpoint when emails arrive

### Expected SendGrid Fields
When SendGrid forwards an email, it sends these fields:
- `from` - Sender email address
- `to` - Recipient email address(es)
- `subject` - Email subject
- `text` - Plain text body
- `html` - HTML body
- `headers` - Raw email headers
- `envelope` - SMTP envelope (JSON)
- `charsets` - Character encodings (JSON)
- `SPF` / `dkim` - Email authentication results
- `attachments` - File attachments (multipart)

## File Storage

### Storage Location
Default: `{AppDirectory}/emails/`

### File Naming Convention
`email_{yyyyMMdd_HHmmss}_{sanitized_from}.enc`

Example: `email_20240115_103045_john.doe_at_example.com.enc`

### File Contents
Encrypted JSON containing:
```json
{
  "from": "sender@example.com",
  "to": "recipient@yourdomain.com",
  "subject": "Email subject",
  "text": "Plain text body",
  "html": "<html>HTML body</html>",
  "envelope": "{...}",
  "charsets": "{...}",
  "receivedAt": "2024-01-15T10:30:45.1234567Z",
  "encrypted": true,
  "attachments": [...]
}
```

### Accessing Stored Emails
Emails are stored encrypted and are **only accessible server-side** via the `IEmailEncryptionService`:

```csharp
// Example: Reading an encrypted email server-side
var decryptedContent = await _encryptionService.ReadEncryptedFileAsync(filePath);
var emailData = JsonSerializer.Deserialize<Dictionary<string, object>>(decryptedContent);
```

**No public API endpoints** are provided for retrieving emails to maintain security.

## Security Considerations

### Current Implementation (Proof of Concept)
? Encryption at rest using Data Protection API  
? No hardcoded secrets  
? No public email retrieval endpoints  
? Anonymous access only for webhook reception  
? Sanitized file paths in responses  

### Production Recommendations
?? **Add webhook authentication**:
   - Validate SendGrid webhook signatures
   - Use shared secret verification
   
?? **Network security**:
   - Whitelist SendGrid IP addresses
   - Use HTTPS only (already implemented)
   
?? **Key management**:
   - Store Data Protection keys in Azure Key Vault
   - Implement key rotation policies
   
?? **Data retention**:
   - Implement automatic cleanup of old emails
   - Add retention policies
   
?? **Processing isolation**:
   - Move email processing to background service
   - Implement queue for async processing

## Next Steps

### Immediate (Proof of Concept Complete) ?
- [x] Receive emails from SendGrid
- [x] Encrypt emails at rest
- [x] Store to disk securely
- [x] Testing scripts created
- [x] Remove public email retrieval endpoints

### Future Enhancements
- [ ] Parse email content into notifications
- [ ] Extract tenant/road information from email
- [ ] Create Address objects from email data
- [ ] Save to TenantManagerService/Roads
- [ ] Implement webhook signature validation
- [ ] Implement data retention/cleanup
- [ ] Add email processing queue/background service
- [ ] Create admin UI for viewing processed notifications

## Testing

### Quick Test
```powershell
# Start application
dotnet run

# Run full test
.\test-post-email.ps1
```

### Expected Results
```
? Email Posted Successfully!
? Email encrypted and saved as: email_20240115_103045_john.doe_at_example.com.enc

Response:
{
  "message": "Email received, encrypted, and saved successfully",
  "file": "email_20240115_103045_john.doe_at_example.com.enc"
}
```

## Endpoints Summary

### Health Check
```bash
GET https://localhost:7070/api/sendgrid/health
```
Response:
```json
{
  "status": "healthy",
  "storagePath": "emails",
  "encryption": "enabled",
  "timestamp": "2024-01-15T10:30:45.1234567Z"
}
```

### Send Email (SendGrid calls this)
```bash
POST https://yourdomain.com/api/sendgrid/inbound
Content-Type: multipart/form-data
```
Response:
```json
{
  "message": "Email received, encrypted, and saved successfully",
  "file": "email_20240115_103045_john.doe_at_example.com.enc"
}
```

## Email Processing Architecture

```
???????????????
?   SendGrid  ?
?   Webhook   ?
???????????????
       ? POST /api/sendgrid/inbound
       ?
???????????????????????????????
?  SendGridController         ?
?  - Receive email            ?
?  - Extract form data        ?
?  - Encrypt with DataProtect ?
?  - Save to disk (.enc)      ?
???????????????????????????????
       ?
       ?
???????????????????????????????
?  Encrypted Storage          ?
?  ./emails/*.enc             ?
?  (Server-side access only)  ?
???????????????????????????????
       ?
       ?
???????????????????????????????
?  Future: Email Parser       ?
?  - Read encrypted files     ?
?  - Parse content            ?
?  - Create notifications     ?
?  - Save to Roads            ?
???????????????????????????????
```

## Git Branch
Branch: `dev/willywil548/email_parsing`
Repository: `https://github.com/willywil548/TheatreRoadTo`

---

**Status**: ? Proof of Concept Complete - Secure Email Reception  
**Ready for**: Email parsing and notification creation conversation
