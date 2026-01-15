#!/bin/bash
# Test script to POST a sample email to the SendGrid endpoint
# This simulates what SendGrid would send when forwarding an email

BASE_URL="https://localhost:7070"

echo -e "\033[0;32mPosting test email to SendGrid endpoint...\033[0m"
echo ""

# Send test email with curl
echo -e "\033[1;33mSending POST request...\033[0m"

RESPONSE=$(curl -k -s -X POST "${BASE_URL}/api/sendgrid/inbound" \
  -F 'headers=Received: from mail.example.com' \
  -F 'dkim={@example.com : pass}' \
  -F 'to=notifications@yourdomain.com' \
  -F 'from=John Doe <john.doe@example.com>' \
  -F 'subject=Road Notification: Upcoming Event on Demo Road' \
  -F 'text=This is a test email sent to verify the SendGrid inbound parse endpoint.

Road: Demo Road
Date: 2024-01-20
Time: 10:00 AM

This is a notification for upcoming event.' \
  -F 'html=<html>
<body>
<h1>Test Email</h1>
<p>This is a <strong>test email</strong> sent to verify the SendGrid inbound parse endpoint.</p>
<ul>
<li>Road: Demo Road</li>
<li>Date: 2024-01-20</li>
<li>Time: 10:00 AM</li>
</ul>
<p>This is a notification for upcoming event.</p>
</body>
</html>' \
  -F 'sender_ip=192.168.1.100' \
  -F 'envelope={"to":["notifications@yourdomain.com"],"from":"john.doe@example.com"}' \
  -F 'charsets={"to":"UTF-8","html":"UTF-8","subject":"UTF-8","from":"UTF-8","text":"UTF-8"}' \
  -F 'SPF=pass')

if [ $? -eq 0 ]; then
    echo -e "\033[0;32m? Email Posted Successfully!\033[0m"
    echo ""
    echo -e "\033[0;36mResponse:\033[0m"
    echo "$RESPONSE" | jq '.'
    
    # Extract filename
    FILENAME=$(echo "$RESPONSE" | jq -r '.file')
    
    echo ""
    echo -e "\033[0;32m? Email encrypted and saved as: $FILENAME\033[0m"
    echo -e "\033[1;33m  Note: Emails are stored encrypted and only accessible server-side\033[0m"
else
    echo -e "\033[0;31m? Failed to post email\033[0m"
fi

echo ""
echo -e "\033[0;32mTest Complete!\033[0m"
echo ""
echo -e "\033[0;36mTo verify the email was saved, check the server logs or the emails directory on the server.\033[0m"
