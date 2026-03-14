# SendGrid Email Inbound Parse - Implementation Summary

## Overview

This implementation provides a secure inbound email pipeline for SendGrid Parse webhook traffic.

Current behavior:
- Validates webhook source.
- Resolves routing from recipient local-part.
- Stores inbound payload encrypted under tenant-scoped storage.
- Optionally materializes road `Address` entries based on routing + domain subdomain rules.
- Global Admins can read/list all tenant emails.
- Tenant Managers can read/list emails for their own tenant scope.

## Components

### 1. SendGrid Controller

**File**: `Theatre_Timeline/Controllers/SendGridController.cs`

**Endpoints**:

| Method | Endpoint | Purpose | Auth |
|--------|----------|---------|------|
| POST | `/api/sendgrid/inbound` | Receive/process email from SendGrid | Anonymous |
| GET | `/api/sendgrid/email/{filename}` | Retrieve a specific stored email | Global Admin or Tenant Manager (own tenant) |
| GET | `/api/sendgrid/emails` | List stored emails | Global Admin or Tenant Manager (own tenant) |
| GET | `/api/sendgrid/health` | Health/status | Anonymous |

**Controller Notes**:
- Delegates processing logic to `ISendGridEmailService`.
- Returns `200 OK` for acknowledged-but-not-saved flows.
- Returns `401` when webhook validation fails.

### 2. SendGrid Email Service

**File**: `Theatre_Timeline/Services/SendGridEmailService.cs`

Core responsibilities:
- Webhook validation orchestration.
- Recipient routing resolution.
- Tenant/demo checks.
- Attachment metadata capture.
- Encrypted persistence.
- Address materialization on roads.

Routing formats (local-part):
- `tenantId@domain` ? tenant-level route.
- `tenantId.roadId@domain` ? tenant+road route (`.` delimiter).

Address creation rule:
- Address creation occurs **only when recipient domain includes a subdomain level** (e.g. `notifications.roadstothere.com`).
- If tenant-level route + subdomain: create address on **all roads** in tenant.
- If tenant+road route + subdomain: create address on **that road only**.

Additional rules:
- Demo tenant is excluded from persistence/address creation.
- Unreadable or unknown tenant routing is acknowledged but not saved.

### 3. SendGrid Inbound Email Model

**File**: `Theatre_Timeline/Models/SendGridInboundEmail.cs`

Features:
- Strongly-typed form binding.
- MIME parsing helpers via MimeKit.
- DKIM/SPF/spam helper methods.
- Envelope parsing support.

### 4. Email Encryption Service

**File**: `Theatre_Timeline/Services/EmailEncryptionService.cs`

- Interface: `IEmailEncryptionService`
- Uses ASP.NET Core Data Protection API.
- Purpose string: `Theatre_TimeLine.EmailStorage.v1`.

### 5. Webhook Validator

**File**: `Theatre_Timeline/Services/SendGridWebhookValidator.cs`

- Interface: `ISendGridWebhookValidator`
- Captures request headers.
- Development mode bypass (`IsDevelopment()`), production indicator checks.

## Configuration

**File**: `Theatre_Timeline/appsettings.json`

```json
{
  "SendGrid": {
    "EnableEncryption": true,
    "RequireIpValidation": false,
    "RequireAuthValidation": false
  },
  "TenantManager": {
    "DemoTenantId": "00000000-0000-0000-0000-3eca75185852"
  }
}
```

| Setting | Description |
|---------|-------------|
| `SendGrid:EnableEncryption` | Health/status indicator for encryption setting |
| `SendGrid:RequireIpValidation` | Health/status indicator for IP validation requirement |
| `SendGrid:RequireAuthValidation` | Health/status indicator for auth validation requirement |
| `TenantManager:DemoTenantId` | Tenant excluded from inbound persistence |

## File Storage

### Location
Tenant-scoped:
- `{TenantRoot}/emails/`

### Naming Convention
`email_{yyyyMMdd_HHmmss}_{sanitized_from}_{guid}.enc`

### StoredAs Format
`{tenantId}/{filename}`

## API Behavior

### POST `/api/sendgrid/inbound`

Possible outcomes:

1. **Unauthorized source**
   - `401 Unauthorized`
   - `{ error: "Invalid webhook source", reason: "..." }`

2. **Acknowledged but not saved** (e.g., unreadable route, unknown tenant, demo tenant)
   - `200 OK`
   - `{ message: "Email acknowledged but not saved", reason: "...", saved: false, ... }`

3. **Saved successfully**
   - `200 OK`
   - `{ message: "Email received, validated, encrypted, and saved successfully", file: "{tenantId}/email_...enc", saved: true, ... }`

### GET `/api/sendgrid/emails`
Lists stored encrypted email summaries. **Global Admin or Tenant Manager required**.
- Global Admin: all tenant emails.
- Tenant Manager: only emails within managed tenant(s).

### GET `/api/sendgrid/email/{filename}`
Retrieves/decrypts a stored email. **Global Admin or Tenant Manager required**.
- Global Admin: any tenant email.
- Tenant Manager: only tenant-qualified filenames in managed tenant scope.

### GET `/api/sendgrid/health`
Returns health + validation configuration snapshot.

## Security Implementation

Current measures:
- Encryption at rest (Data Protection API)
- Webhook validation
- PII masking in logs
- Log sanitization
- Path traversal protection on retrieval
- Admin-only read/list access
- Over-posting prevention in model (`[BindNever]`)

## Architecture (Current)

```
SendGrid Inbound Parse
        |
        v
POST /api/sendgrid/inbound
        |
        v
SendGridWebhookValidator
        |
        v
SendGridEmailService
  - resolve tenant / tenant.road
  - demo exclusion
  - enrich + encrypt + persist
  - optional address creation (subdomain required)
        |
        +--> tenant storage: {tenantRoot}/emails/*.enc
        |
        +--> road address creation
              - tenant route -> all roads
              - tenant.road route -> single road
```

## SendGrid Configuration Notes

To use road/tenant routing, configure inbound parse mailbox local-parts accordingly:
- Tenant only: `{tenantGuid}`
- Tenant + road: `{tenantGuid}.{roadGuid}`

To trigger address creation, route through a domain with subdomain level (for example `notifications.example.com`).

## Local Development Testing

For local testing, you can still post directly to the inbound API endpoint instead of using SendGrid delivery.

Example:

```bash
curl -X POST https://localhost:7070/api/sendgrid/inbound \
  -F "from=Dev Tester <dev@example.com>" \
  -F "to=00000000-0000-0000-0000-3eca75185852@notifications.roadstothere.local" \
  -F "subject=Local tenant-level test" \
  -F "text=This should route to tenant processing" \
  -F "SPF=pass" \
  -F "dkim={@example.com : pass}"
```

Road-level example (`tenant.road`):

```bash
curl -X POST https://localhost:7070/api/sendgrid/inbound \
  -F "from=Dev Tester <dev@example.com>" \
  -F "to={tenantGuid}.{roadGuid}@notifications.roadstothere.local" \
  -F "subject=Local road-level test" \
  -F "text=This should create one road address" \
  -F "SPF=pass" \
  -F "dkim={@example.com : pass}"
```

Notes:
- Use real tenant/road GUIDs from your local data.
- Address creation requires a subdomain in the recipient domain (for example `notifications.*`).
- In Development environment, webhook validator allows local testing while still capturing headers.

---

**Status**: ? Tenant/Road-routed inbound processing enabled

