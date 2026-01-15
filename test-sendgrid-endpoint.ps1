# Test script for SendGrid endpoint
# Run this after starting the application with 'dotnet run'

$baseUrl = "http://localhost:5000"

Write-Host "Testing SendGrid Endpoint..." -ForegroundColor Green
Write-Host ""

# Test 1: Health Check
Write-Host "1. Testing Health Check..." -ForegroundColor Yellow
try {
    $healthResponse = Invoke-RestMethod -Uri "$baseUrl/api/sendgrid/health" -Method Get
    Write-Host "? Health Check Passed" -ForegroundColor Green
    $healthResponse | ConvertTo-Json
} catch {
    Write-Host "? Health Check Failed: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "2. Testing Email Inbound..." -ForegroundColor Yellow

# Create a multipart form
$boundary = [System.Guid]::NewGuid().ToString()
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
    'Test Email from PowerShell',
    "--$boundary",
    'Content-Disposition: form-data; name="text"',
    '',
    'This is a test email sent via PowerShell to verify the SendGrid endpoint.',
    "--$boundary",
    'Content-Disposition: form-data; name="html"',
    '',
    '<p>This is a <strong>test email</strong> sent via PowerShell.</p>',
    "--$boundary--"
)

$body = $bodyLines -join "`r`n"

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
    $listResponse | ConvertTo-Json -Depth 3
    
    if ($listResponse.emails.Count -gt 0) {
        $firstEmail = $listResponse.emails[0].filename
        Write-Host ""
        Write-Host "4. Retrieving Email: $firstEmail" -ForegroundColor Yellow
        $emailResponse = Invoke-RestMethod -Uri "$baseUrl/api/sendgrid/email/$firstEmail" -Method Get
        $emailResponse | ConvertTo-Json -Depth 3
    }
} catch {
    Write-Host "? Email Test Failed: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "Test Complete!" -ForegroundColor Green
