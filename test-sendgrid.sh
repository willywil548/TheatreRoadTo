#!/bin/bash
# Test script for SendGrid endpoint over HTTPS
# Usage: ./test-sendgrid.sh [http|https] [port]

PROTOCOL=${1:-https}
PORT=${2:-7000}
BASE_URL="${PROTOCOL}://localhost:${PORT}"

# Colors
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

echo -e "${GREEN}Testing SendGrid Endpoint at ${BASE_URL}...${NC}"
echo ""

# Test 1: Health Check
echo -e "${YELLOW}1. Testing Health Check...${NC}"
HEALTH_RESPONSE=$(curl -k -s "${BASE_URL}/api/sendgrid/health")
if [ $? -eq 0 ]; then
    echo -e "${GREEN}? Health Check Passed${NC}"
    echo "$HEALTH_RESPONSE" | jq '.'
else
    echo -e "${RED}? Health Check Failed${NC}"
    echo -e "${RED}Make sure the application is running!${NC}"
    exit 1
fi

echo ""
echo -e "${YELLOW}2. Testing Email Inbound...${NC}"

# Send test email
SEND_RESPONSE=$(curl -k -s -X POST "${BASE_URL}/api/sendgrid/inbound" \
  -F "from=sender@example.com" \
  -F "to=recipient@yourdomain.com" \
  -F "subject=Test Email via HTTPS" \
  -F "text=This is a test email sent via curl to verify the SendGrid endpoint over HTTPS." \
  -F "html=<html><body><p>This is a <strong>test email</strong> sent via curl.</p><p>Using HTTPS</p></body></html>" \
  -F "envelope={\"to\":[\"recipient@yourdomain.com\"],\"from\":\"sender@example.com\"}" \
  -F "charsets={\"to\":\"UTF-8\",\"html\":\"UTF-8\",\"subject\":\"UTF-8\",\"from\":\"UTF-8\",\"text\":\"UTF-8\"}")

if [ $? -eq 0 ]; then
    echo -e "${GREEN}? Email Received and Saved${NC}"
    echo "$SEND_RESPONSE" | jq '.'
else
    echo -e "${RED}? Email Test Failed${NC}"
    exit 1
fi

echo ""
echo -e "${YELLOW}3. Listing Emails...${NC}"
LIST_RESPONSE=$(curl -k -s "${BASE_URL}/api/sendgrid/emails")
EMAIL_COUNT=$(echo "$LIST_RESPONSE" | jq -r '.count')
echo -e "${CYAN}Found ${EMAIL_COUNT} email(s)${NC}"
echo "$LIST_RESPONSE" | jq '.'

# Get the first email filename
FIRST_EMAIL=$(echo "$LIST_RESPONSE" | jq -r '.emails[0].filename')

if [ "$FIRST_EMAIL" != "null" ] && [ -n "$FIRST_EMAIL" ]; then
    echo ""
    echo -e "${YELLOW}4. Retrieving and Decrypting Email: ${FIRST_EMAIL}${NC}"
    EMAIL_RESPONSE=$(curl -k -s "${BASE_URL}/api/sendgrid/email/${FIRST_EMAIL}")
    if [ $? -eq 0 ]; then
        echo -e "${GREEN}? Email Retrieved and Decrypted Successfully${NC}"
        echo "$EMAIL_RESPONSE" | jq '.'
    else
        echo -e "${RED}? Failed to retrieve email${NC}"
    fi
fi

echo ""
echo -e "${GREEN}Test Complete!${NC}"
echo ""
echo -e "${CYAN}Email storage location:${NC}"
echo "$HEALTH_RESPONSE" | jq -r '.storagePath'
