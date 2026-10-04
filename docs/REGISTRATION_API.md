# Registration API Documentation

Complete documentation for the user registration authentication flow in Bee Logistics Backend.

## Base URL

- **Development**: `https://localhost:5001/api/auth`
- **Production**: `https://api.yourdomain.com/api/auth`

## Overview

The registration flow supports two methods:
1. **OTP-based registration** (recommended): Email verification via OTP before account creation
2. **Email link verification**: Traditional email verification link after registration

The recommended flow is OTP-based registration for better security and user experience.

---

## Registration Flow (Recommended: OTP-Based)

### Step 1: Send OTP for Email Verification

**Endpoint**: `POST /api/auth/send-otp`

**Description**: Sends a one-time password (OTP) to the user's email for verification before account creation.

**Authentication**: Not required (Anonymous)

**Request Body**:
```json
{
  "email": "user@example.com"
}
```

**Response** (Success):
```json
{
  "success": true,
  "message": "If this email is valid, a verification code has been sent. Please check your inbox."
}
```

**Response** (Error - Email Locked):
```json
{
  "success": false,
  "message": "Too many failed attempts. Please try again later."
}
```

**Status Codes**:
- `200 OK`: OTP sent (generic message to prevent email enumeration)
- `400 Bad Request`: Invalid request or email locked

**Security Features**:
- Rate limited: 5 requests per email per 15 minutes + 10 requests per IP per hour
- Generic response message to prevent email enumeration attacks
- Email normalization (lowercase, trim)

**Notes**:
- The response message is generic even if the email already exists (security measure)
- OTP expires after 10 minutes
- Maximum 5 verification attempts before email lock (30-minute lock)

---

### Step 2: Verify OTP

**Endpoint**: `POST /api/auth/verify-otp`

**Description**: Verifies the OTP code sent to the user's email. This marks the email as verified for registration but does NOT create the account.

**Authentication**: Not required (Anonymous)

**Request Body**:
```json
{
  "email": "user@example.com",
  "otp": "123456"
}
```

**Response** (Success):
```json
{
  "success": true,
  "message": "Email verified. You can now complete registration."
}
```

**Response** (Error):
```json
{
  "success": false,
  "message": "Invalid or expired OTP"
}
```

**Status Codes**:
- `200 OK`: OTP verified successfully
- `400 Bad Request`: Invalid OTP, expired OTP, or email locked

**Security Features**:
- Rate limited: 10 attempts per email per 10 minutes
- Email lock after too many failed attempts
- OTP is single-use (consumed after verification)

**Important**: After successful OTP verification, you must call the `/register` endpoint with the same email to complete account creation.

---

### Step 3: Resend OTP (Optional)

**Endpoint**: `POST /api/auth/resend-otp`

**Description**: Invalidates the previous OTP and sends a new one to the user's email.

**Authentication**: Not required (Anonymous)

**Request Body**:
```json
{
  "email": "user@example.com"
}
```

**Response** (Success):
```json
{
  "success": true,
  "message": "If this email is valid, a new verification code has been sent. Please check your inbox."
}
```

**Status Codes**:
- `200 OK`: New OTP sent
- `400 Bad Request`: Email locked or invalid request

**Security Features**:
- Rate limited: 3 requests per email per 15 minutes
- Previous OTP is invalidated when a new one is sent

---

### Step 4: Register Account

**Endpoint**: `POST /api/auth/register`

**Description**: Creates a new user account. If email was verified via OTP (Step 2), the account is created with `EmailConfirmed = true`. Otherwise, an email verification link is sent.

**Authentication**: Not required (Anonymous)

**Request Body**:
```json
{
  "email": "user@example.com",
  "password": "SecurePassword123!",
  "fullName": "John Doe",
  "role": "Customer",
  "phoneNumber": "+1234567890",
  "companyId": "00000000-0000-0000-0000-000000000000",
  "referralCode": "REF123",
  "securityQuestion1": {
    "questionId": 1,
    "answer": "Fluffy"
  },
  "securityQuestion2": {
    "questionId": 5,
    "answer": "Toyota"
  },
  "securityQuestion3": {
    "questionId": 10,
    "answer": "Alice"
  }
}
```

**Request Fields**:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `email` | string | Yes | User's email address (must be unique) |
| `password` | string | Yes | User's password (must meet complexity requirements) |
| `fullName` | string | Yes | User's full name |
| `role` | string | No | User role. Default: `"Customer"`. Valid values: `"Customer"`, `"Driver"` |
| `phoneNumber` | string | No | User's phone number |
| `companyId` | GUID | No | Company ID (obsolete, kept for API compatibility) |
| `referralCode` | string | No | Referral code for rewards |
| `securityQuestion1` | object | No | First security question (for password recovery) |
| `securityQuestion2` | object | No | Second security question (optional) |
| `securityQuestion3` | object | No | Third security question (optional) |

**Security Question Object**:
```json
{
  "questionId": 1,
  "answer": "Your answer here"
}
```

**Response** (Success - Email Verified via OTP):
```json
{
  "success": true,
  "message": "User registered successfully. Please check your email to verify your account.",
  "requiresEmailVerification": false,
  "email": "user@example.com"
}
```

**Response** (Success - Email Verification Required):
```json
{
  "success": true,
  "message": "User registered successfully. Please check your email to verify your account.",
  "requiresEmailVerification": true,
  "email": "user@example.com"
}
```

**Response** (Error - Email Already Registered):
```json
{
  "success": false,
  "message": "This email is already registered. Please use a different email or sign in.",
  "errorReference": "A1B2C3D4"
}
```

**Response** (Error - Validation Failed):
```json
{
  "success": false,
  "message": "Password does not meet requirements.",
  "errorReference": "E5F6G7H8"
}
```

**Status Codes**:
- `200 OK`: Registration successful
- `400 Bad Request`: Validation error, email already exists, or invalid security questions
- `500 Internal Server Error`: Server error (error reference provided for tracking)

**Password Requirements**:
- Minimum 8 characters (enforced by Identity framework)
- Additional complexity requirements may apply based on Identity configuration

**Available User Roles**:
- `"Customer"`: Regular customer (default)
- `"Driver"`: Driver role (requires additional onboarding)

**Security Features**:
- Email normalization (lowercase, trim)
- Security answers are hashed using SHA256 before storage
- Error references for server-side tracking (prevents information leakage)
- Generic error messages to prevent enumeration

**Onboarding Status**:
- `Customer`: `IsOnboarded = true` (immediately)
- `Driver`: `IsOnboarded = false` (requires driver application approval)
- `Owner`: `IsOnboarded = false` (requires onboarding)

**Referral Code Processing**:
- Referral codes are processed asynchronously
- Registration does not fail if referral processing fails (logged only)

---

## Alternative Registration Flow (Email Link Verification)

If you skip OTP verification, you can register directly and verify via email link:

### Step 1: Register Account

**Endpoint**: `POST /api/auth/register`

Same as Step 4 above, but without calling `verify-otp` first.

**Result**: Account is created with `EmailConfirmed = false`, and a verification email is sent.

### Step 2: Verify Email

**Endpoint**: `POST /api/auth/verify-email`

**Description**: Verifies email address using the token from the verification email.

**Authentication**: Not required (Anonymous)

**Request Body**:
```json
{
  "email": "user@example.com",
  "token": "base64-encoded-token-from-email"
}
```

**Response** (Success):
```json
{
  "success": true,
  "message": "Email verified successfully"
}
```

**Response** (Error):
```json
{
  "success": false,
  "message": "Invalid or expired verification token"
}
```

**Status Codes**:
- `200 OK`: Email verified
- `400 Bad Request`: Invalid token or email already verified

---

## Supporting Endpoints

### Get Available Security Questions

**Endpoint**: `GET /api/auth/security-questions`

**Description**: Returns all available security questions that users can choose from during registration.

**Authentication**: Not required (Anonymous)

**Response**:
```json
{
  "success": true,
  "questions": [
    {
      "id": 1,
      "question": "What was the name of your first pet?"
    },
    {
      "id": 2,
      "question": "What city were you born in?"
    },
    {
      "id": 3,
      "question": "What was the name of your elementary school?"
    }
    // ... 20 total questions
  ]
}
```

**Available Questions** (20 total):
1. What was the name of your first pet?
2. What city were you born in?
3. What was the name of your elementary school?
4. What was your mother's maiden name?
5. What was the make of your first car?
6. What was your childhood nickname?
7. What is the name of your favorite teacher?
8. What street did you grow up on?
9. What was your favorite food as a child?
10. What is the name of your best friend from childhood?
11. What was the name of your first employer?
12. What is your favorite movie?
13. What was the model of your first car?
14. What is your favorite book?
15. What is the name of the hospital where you were born?
16. What is your favorite sports team?
17. What was your favorite subject in school?
18. What is the name of your favorite restaurant?
19. What was the name of your first boss?
20. What is your favorite vacation destination?

---

### Check Email Availability

**Endpoint**: `POST /api/auth/check-email`

**Description**: Checks if an email address is available for registration. Rate-limited to prevent email enumeration attacks.

**Authentication**: Not required (Anonymous)

**Request Body**:
```json
{
  "email": "user@example.com"
}
```

**Response** (Email Available):
```json
{
  "success": true,
  "available": true,
  "message": "Email is available"
}
```

**Response** (Email Not Available):
```json
{
  "success": true,
  "available": false,
  "message": "This email is already registered as Customer. Each email can only be registered with one role.",
  "existingRole": "Customer"
}
```

**Response** (Invalid Format):
```json
{
  "success": true,
  "available": false,
  "message": "Invalid email format"
}
```

**Status Codes**:
- `200 OK`: Request processed (always returns success: true)
- `400 Bad Request`: Email field missing

**Security Features**:
- Rate limited to prevent enumeration attacks
- Email normalization (lowercase, trim)

---

### Get Registration Status

**Endpoint**: `GET /api/auth/registration-status?email=user@example.com`

**Description**: Checks the registration status for an email address (whether email is verified and registration is complete).

**Authentication**: Not required (Anonymous)

**Query Parameters**:
- `email` (required): Email address to check

**Response** (Email Not Registered):
```json
{
  "success": true,
  "emailVerified": false,
  "registrationComplete": false,
  "canResume": false
}
```

**Response** (Email Verified, Registration Incomplete):
```json
{
  "success": true,
  "emailVerified": true,
  "registrationComplete": false,
  "canResume": true
}
```

**Response** (Registration Complete):
```json
{
  "success": true,
  "emailVerified": true,
  "registrationComplete": true,
  "canResume": false
}
```

**Status Codes**:
- `200 OK`: Status retrieved
- `400 Bad Request`: Email parameter missing

---

### Resend Verification Email

**Endpoint**: `POST /api/auth/resend-verification`

**Description**: Resends the email verification link to a user who hasn't verified their email yet.

**Authentication**: Not required (Anonymous)

**Request Body**:
```json
{
  "email": "user@example.com"
}
```

**Response** (Success):
```json
{
  "success": true,
  "message": "Verification email sent. Please check your inbox."
}
```

**Response** (Already Verified):
```json
{
  "success": true,
  "message": "Email is already verified"
}
```

**Status Codes**:
- `200 OK`: Email sent or already verified
- `400 Bad Request`: Invalid request

**Security Features**:
- Generic response even if email doesn't exist (prevents enumeration)

---

## Obsolete Endpoint (Backward Compatibility)

### Verify OTP and Register (Combined)

**Endpoint**: `POST /api/auth/verify-otp-and-register`

**Description**: ⚠️ **Obsolete** - This endpoint combines OTP verification and registration in one step. Use the separate `verify-otp` and `register` endpoints instead.

**Status**: Deprecated - kept for backward compatibility only

**Recommendation**: Use the 3-step flow: `send-otp` → `verify-otp` → `register`

---

## Complete Registration Flow Example

### OTP-Based Registration (Recommended)

```javascript
// Step 1: Send OTP
const sendOtpResponse = await fetch('/api/auth/send-otp', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: 'user@example.com' })
});

// Step 2: Verify OTP
const verifyOtpResponse = await fetch('/api/auth/verify-otp', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ 
    email: 'user@example.com',
    otp: '123456' // OTP from email
  })
});

// Step 3: Register Account
const registerResponse = await fetch('/api/auth/register', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    email: 'user@example.com',
    password: 'SecurePassword123!',
    fullName: 'John Doe',
    role: 'Customer',
    phoneNumber: '+1234567890',
    securityQuestion1: {
      questionId: 1,
      answer: 'Fluffy'
    }
  })
});

// Step 4: Login (after successful registration)
const loginResponse = await fetch('/api/auth/login', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    email: 'user@example.com',
    password: 'SecurePassword123!'
  })
});
```

---

## Error Handling

All endpoints return standardized error responses:

```json
{
  "success": false,
  "message": "Human-readable error message",
  "errorReference": "A1B2C3D4" // Optional: for server-side tracking
}
```

**Common Error Scenarios**:

1. **Email Already Registered**:
   - Message: "This email is already registered. Please use a different email or sign in."
   - Status: `400 Bad Request`

2. **Invalid OTP**:
   - Message: "Invalid or expired OTP"
   - Status: `400 Bad Request`

3. **Email Locked**:
   - Message: "Too many failed attempts. Please try again later."
   - Status: `400 Bad Request`

4. **Password Requirements Not Met**:
   - Message: "Password does not meet requirements."
   - Status: `400 Bad Request`

5. **Invalid Security Question**:
   - Message: "Invalid security question selected."
   - Status: `400 Bad Request`

---

## Security Considerations

### Rate Limiting

- **OTP Send**: 5 requests per email per 15 minutes + 10 requests per IP per hour
- **OTP Verify**: 10 attempts per email per 10 minutes
- **OTP Resend**: 3 requests per email per 15 minutes
- **Email Check**: Rate limited to prevent enumeration

### Email Enumeration Prevention

- Generic success messages (don't reveal if email exists)
- Same response format for existing and non-existing emails
- Rate limiting on email-based operations

### Password Security

- Passwords are hashed using ASP.NET Core Identity password hashing
- Security answers are hashed using SHA256
- Minimum password length: 8 characters

### OTP Security

- OTP expires after 10 minutes
- Single-use OTPs (consumed after verification)
- Maximum 5 failed attempts before email lock (30-minute lock)
- OTPs are stored securely in cache with expiration

---

## Notes

1. **Email Normalization**: All emails are normalized (lowercase, trimmed) before processing
2. **Role Defaults**: If `role` is not provided, defaults to `"Customer"`
3. **Onboarding**: Drivers and Owners require additional onboarding steps after registration
4. **Referral Codes**: Referral code processing is asynchronous and does not block registration
5. **CompanyId**: The `companyId` field is obsolete (all drivers are independent) but kept for API compatibility
6. **Security Questions**: Users can set up to 3 security questions (all optional) for password recovery

---

## Related Endpoints

- **Login**: `POST /api/auth/login` - Authenticate after registration
- **Password Reset**: `POST /api/auth/forgot-password` - Reset password using security questions
- **Change Password**: `POST /api/auth/change-password` - Change password (requires authentication)

---

## Version History

- **Current Version**: Supports OTP-based registration flow
- **Deprecated**: `verify-otp-and-register` endpoint (use separate endpoints instead)


