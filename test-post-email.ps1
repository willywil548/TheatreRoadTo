# Test script to POST a sample email to the SendGrid endpoint
# This simulates what SendGrid would send when forwarding an email

$baseUrl = "https://localhost:7070"

Write-Host "Posting test email to SendGrid endpoint..." -ForegroundColor Green
Write-Host ""

# Skip certificate validation for development
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

# Create multipart form data
$boundary = [System.Guid]::NewGuid().ToString()
$LF = "`r`n"

# Sample email data that mimics what SendGrid sends
$bodyLines = @(
    "--$boundary",
    'Content-Disposition: form-data; name="headers"',
    '',
    'Received: from mail.example.com',
    "--$boundary",
    'Content-Disposition: form-data; name="dkim"',
    '',
    '{@example.com : pass}',
    "--$boundary",
    'Content-Disposition: form-data; name="email"',
    '',
    'Test Email',
    "--$boundary",
    'Content-Disposition: form-data; name="to"',
    '',
    'notifications@yourdomain.com',
    "--$boundary",
    'Content-Disposition: form-data; name="cc"',
    '',
    '',
    "--$boundary",
    'Content-Disposition: form-data; name="from"',
    '',
    'John Doe <john.doe@example.com>',
    "--$boundary",
    'Content-Disposition: form-data; name="text"',
    '',
    'This is a test email sent to verify the SendGrid inbound parse endpoint.',
    'It contains plain text content.',
    '',
    'Road: Demo Road',
    'Date: 2024-01-20',
    'Time: 10:00 AM',
    '',
    'This is a notification for upcoming event.',
    "--$boundary",
    'Content-Disposition: form-data; name="html"',
    '',
    '<html>',
    '<body>',
    '<h1>Test Email</h1>',
    '<p>This is a <strong>test email</strong> sent to verify the SendGrid inbound parse endpoint.</p>',
    '<p>It contains HTML content.</p>',
    '<ul>',
    '<li>Road: Demo Road</li>',
    '<li>Date: 2024-01-20</li>',
    '<li>Time: 10:00 AM</li>',
    '</ul>',
    '<p>This is a notification for upcoming event.</p>',
    '</body>',
    '</html>',
    "--$boundary",
    'Content-Disposition: form-data; name="sender_ip"',
    '',
    '192.168.1.100',
    "--$boundary",
    'Content-Disposition: form-data; name="spam_report"',
    '',
    'Spam detection results: Not spam',
    "--$boundary",
    'Content-Disposition: form-data; name="envelope"',
    '',
    '{"to":["notifications@yourdomain.com"],"from":"john.doe@example.com"}',
    "--$boundary",
    'Content-Disposition: form-data; name="subject"',
    '',
    'Road Notification: Upcoming Event on Demo Road',
    "--$boundary",
    'Content-Disposition: form-data; name="spam_score"',
    '',
    '0.1',
    "--$boundary",
    'Content-Disposition: form-data; name="charsets"',
    '',
    '{"to":"UTF-8","html":"UTF-8","subject":"UTF-8","from":"UTF-8","text":"UTF-8"}',
    "--$boundary",
    'Content-Disposition: form-data; name="SPF"',
    '',
    'pass',
    "--$boundary--"
)

$body = $bodyLines -join $LF

try {
    Write-Host "Sending POST request..." -ForegroundColor Yellow
    $response = Invoke-RestMethod -Uri "$baseUrl/api/sendgrid/inbound" `
        -Method Post `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Body $body
    
    Write-Host "? Email Posted Successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Response:" -ForegroundColor Cyan
    $response | ConvertTo-Json -Depth 3
    
    $savedFile = $response.file
    
    Write-Host ""
    Write-Host "? Email encrypted and saved as: $savedFile" -ForegroundColor Green
    Write-Host "  Note: Emails are stored encrypted and only accessible server-side" -ForegroundColor Yellow
    
} catch {
    Write-Host "? Failed to post email" -ForegroundColor Red
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $reader.BaseStream.Position = 0
        $reader.DiscardBufferedData()
        $responseBody = $reader.ReadToEnd()
        Write-Host "Response: $responseBody" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "Test Complete!" -ForegroundColor Green
Write-Host ""
Write-Host "To verify the email was saved, check the server logs or the emails directory on the server." -ForegroundColor Cyan
