# Test script for SendGrid endpoint over HTTPS
# Run this after starting the application with 'dotnet run'

# Configuration
$useHttps = $true
$port = if ($useHttps) { 7000 } else { 5000 }
$protocol = if ($useHttps) { "https" } else { "http" }
$baseUrl = "${protocol}://localhost:${port}"

# For development, ignore self-signed certificate errors
if ($useHttps) {
    # Skip certificate validation for development only
    # WARNING: Do NOT use this in production!
    add-type @"
using System.Net;
using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsPolicy : ICertificatePolicy {
    public bool CheckValidationResult(
        ServicePoint srvPoint, X509Certificate certificate,
        WebRequest request, int certificateProblem) {
        return true;
    }
}
"@
    [System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

Write-Host "Testing SendGrid Endpoint at $baseUrl..." -ForegroundColor Green
Write-Host ""

# Test 1: Health Check
Write-Host "1. Testing Health Check..." -ForegroundColor Yellow
try {
    $healthResponse = Invoke-RestMethod -Uri "$baseUrl/api/sendgrid/health" -Method Get
    Write-Host "? Health Check Passed" -ForegroundColor Green
    $healthResponse | ConvertTo-Json
} catch {
    Write-Host "? Health Check Failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   Make sure the application is running!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "2. Testing Email Inbound..." -ForegroundColor Yellow

# Create a multipart form
$boundary = [System.Guid]::NewGuid().ToString()
$LF = "`r`n"

$bodyLines = @(
    "--$boundary",
    'Content-Disposition: form-data; name="from"',
    '',
    'sender@example.com',
    "--$boundary",
    'Content-Disposition: form-data; name="to"',
    '',
    'recipient@yourdomain.com',
    "--$boundary",
    'Content-Disposition: form-data; name="subject"',
    '',
    'Test Email from PowerShell (HTTPS)',
    "--$boundary",
    'Content-Disposition: form-data; name="text"',
    '',
    'This is a test email sent via PowerShell to verify the SendGrid endpoint over HTTPS.',
    "--$boundary",
    'Content-Disposition: form-data; name="html"',
    '',
    '<html><body><p>This is a <strong>test email</strong> sent via PowerShell.</p><p>Using HTTPS: ' + $useHttps + '</p></body></html>',
    "--$boundary",
    'Content-Disposition: form-data; name="envelope"',
    '',
    '{"to":["recipient@yourdomain.com"],"from":"sender@example.com"}',
    "--$boundary",
    'Content-Disposition: form-data; name="charsets"',
    '',
    '{"to":"UTF-8","html":"UTF-8","subject":"UTF-8","from":"UTF-8","text":"UTF-8"}',
    "--$boundary--"
)

$body = $bodyLines -join $LF

try {
    $response = Invoke-RestMethod -Uri "$baseUrl/api/sendgrid/inbound" `
        -Method Post `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Body $body
    
    Write-Host "? Email Received and Saved" -ForegroundColor Green
    $response | ConvertTo-Json
    
    Write-Host ""
    Write-Host "3. Listing Emails..." -ForegroundColor Yellow
    $listResponse = Invoke-RestMethod -Uri "$baseUrl/api/sendgrid/emails" -Method Get
    Write-Host "Found $($listResponse.count) email(s)" -ForegroundColor Cyan
    $listResponse | ConvertTo-Json -Depth 3
    
    if ($listResponse.emails.Count -gt 0) {
        $firstEmail = $listResponse.emails[0].filename
        Write-Host ""
        Write-Host "4. Retrieving and Decrypting Email: $firstEmail" -ForegroundColor Yellow
        $emailResponse = Invoke-RestMethod -Uri "$baseUrl/api/sendgrid/email/$firstEmail" -Method Get
        Write-Host "? Email Retrieved and Decrypted Successfully" -ForegroundColor Green
        $emailResponse | ConvertTo-Json -Depth 3
    }
} catch {
    Write-Host "? Email Test Failed" -ForegroundColor Red
    Write-Host "   Error: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $reader.BaseStream.Position = 0
        $reader.DiscardBufferedData()
        $responseBody = $reader.ReadToEnd()
        Write-Host "   Response: $responseBody" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Test Complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Email storage location:" -ForegroundColor Cyan
Write-Host "  $($healthResponse.storagePath)" -ForegroundColor White
