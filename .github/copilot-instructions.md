# Copilot Instructions

## Project Guidelines
- When user asks to 'review the project', provide analysis only and do not implement or modify files unless explicitly instructed. Confirm with the user before making any code changes; do not implement fixes unless explicitly asked.

## Code Documentation
- Add XML documentation comments for internal and public accessors when creating or modifying code.
- Include explanatory inline comments throughout the code to assist readers in understanding the logic and flow.

## Address Types
- Treat address types as: poll, plain text notification, or web links.
- Only YouTube URLs should map to video/web-link type; non-YouTube links should remain plain text notification content.