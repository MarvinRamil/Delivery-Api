# Registration API – Frontend Implementation Guide

This document is **scanned from the actual backend code** and describes all registration-related endpoints, request/response shapes, validation rules, and how to implement them in the frontend.

**Base URL:** `https://your-api-domain.com` (e.g. `https://localhost:5001` in dev)  
**Auth prefix:** All endpoints below are under `/api/auth` unless noted.

---

## Table of Contents

1. [Registration flow overview](#registration-flow-overview)
2. [Endpoints reference](#endpoints-reference)
3. [Request / response contracts](#request--response-contracts)
4. [Validation rules](#validation-rules)
5. [Rate limits](#rate-limits)
6. [Frontend implementation steps](#frontend-implementation-steps)
7. [Example: Customer registration](#example-customer-registration)
8. [Example: Driver registration (two steps)](#example-driver-registration-two-steps)

---

## Registration flow overview

| User type   | Flow |
|------------|------|
| **Customer** | 1. Optional: check email → 2. POST register → 3. User verifies email (link) → 4. User can login |
| **Driver**   | 1. Optional: check email → 2. POST register (role=Driver) → 3. User verifies email → 4. GET registration-status → 5. POST register/driver/complete (documents) → 6. User can login |

**Roles (per BREAKING_CHANGES / current code):**

- `Customer` – end users who book (default if role omitted).
- `Driver` – drivers who accept deliveries. After register they must verify email, then call **complete driver registration** with documents.

---

## Endpoints reference

All of these were taken from `AuthController.cs`.

| Method | Endpoint | Auth | Purpose |
|--------|----------|------|--------|
| POST   | `/api/auth/check-email` | No | Check if email is available before showing signup form |
| POST   | `/api/auth/register` | No | Create account (Customer or Driver) |
| GET    | `/api/auth/registration-status?email={email}` | No | For drivers: see if email verified and if onboarding is complete |
| POST   | `/api/auth/verify-email` | No | Confirm email using token from verification link |
| POST   | `/api/auth/resend-verification` | No | Resend verification email |
| GET    | `/api/auth/security-questions` | No | List security questions (for signup / forgot password) |
| POST   | `/api/auth/register/driver/complete` | No | Driver only: upload license + selfie, complete onboarding |

---

## Request / response contracts

### 1. POST `/api/auth/check-email`

**Request body (JSON):**

```json
{
  "email": "user@example.com"
}
```

**Success (200):**

- Email **available:**  
  `{ "success": true, "available": true, "message": "Email is available" }`
- Email **taken:**  
  `{ "success": true, "available": false, "message": "This email is already registered as {Role}. Each email can only be registered with one role.", "existingRole": "Customer" }`
- **Invalid format:**  
  `{ "success": true, "available": false, "message": "Invalid email format" }`

**Other:** `400` if body missing or `email` empty (e.g. `{ "success": false, "message": "Email is required" }`).

---

### 2. POST `/api/auth/register`

**Request body (JSON):**

```ts
{
  email: string;           // required
  password: string;        // required
  fullName: string;        // required
  role?: string;           // optional, "Customer" | "Driver". Default: "Customer"
  companyId?: string;      // optional UUID
  referralCode?: string;   // optional
  phoneNumber?: string;    // optional
  securityQuestion1?: { questionId: number; answer: string };  // optional
  securityQuestion2?: { questionId: number; answer: string };  // optional
  securityQuestion3?: { questionId: number; answer: string };  // optional
}
```

**Security question IDs:** `1–20` (see GET `/api/auth/security-questions`). If you send any `securityQuestionN`, both `questionId` and `answer` must be valid and non-empty.

**Success (200):**

```json
{
  "success": true,
  "message": "User registered successfully. Please check your email to verify your account.",
  "requiresEmailVerification": true,
  "email": "user@example.com"
}
```

**Errors:**

- `400` – Email already registered:  
  `{ "success": false, "message": "This email is already registered. Please use a different email or sign in.", "errorReference": "ABC12DEF" }`
- `400` – Validation (e.g. invalid security question, password rules):  
  `{ "success": false, "message": "Password does not meet requirements." }` or similar, plus optional `errorReference`
- `500` – Server error:  
  `{ "success": false, "message": "An unexpected error occurred during registration. Please try again later.", "errorReference": "..." }`

---

### 3. GET `/api/auth/registration-status`

**Query:** `email` (required).

**Example:** `GET /api/auth/registration-status?email=driver@example.com`

**Success (200):**

- Email **not registered:**  
  `{ "success": true, "emailVerified": false, "registrationComplete": false, "canResume": false }`
- Email **registered but not verified:**  
  `{ "success": true, "emailVerified": false, "registrationComplete": false, "canResume": false }`
- Email **verified, driver not yet completed:**  
  `{ "success": true, "emailVerified": true, "registrationComplete": false, "canResume": true }`
- **Fully complete:**  
  `{ "success": true, "emailVerified": true, "registrationComplete": true, "canResume": false }`

**Use in frontend:** After driver verifies email, call this. If `canResume === true`, show “Complete registration” (documents) step. If `registrationComplete === true`, redirect to login.

**Other:** `400` if `email` missing.

---

### 4. POST `/api/auth/verify-email`

Used when user clicks the link in the verification email. Backend expects the token your app receives (e.g. from query params).

**Request body (JSON):**

```json
{
  "email": "user@example.com",
  "token": "<base64url-encoded-token-from-email-link>"
}
```

**Success (200):**

- Just verified:  
  `{ "success": true, "message": "Email verified successfully" }`
- Already verified:  
  `{ "success": true, "message": "Email is already verified" }`

**Errors:** `400` – e.g. `{ "success": false, "message": "Invalid or expired verification token" }`.

---

### 5. POST `/api/auth/resend-verification`

**Request body (JSON):**

```json
{
  "email": "user@example.com"
}
```

**Success (200):**  
`{ "success": true, "message": "Verification email sent. Please check your inbox." }`  
(or “Email is already verified” / generic message if email not found – backend does not reveal existence).

**Errors:** `400` e.g. “Failed to send verification email. Please try again later.”

---

### 6. GET `/api/auth/security-questions`

**No body.** No auth.

**Success (200):**

```json
{
  "success": true,
  "questions": [
    { "id": 1, "question": "What was the name of your first pet?" },
    { "id": 2, "question": "What city were you born in?" }
  ]
}
```

IDs are 1–20. Use `questions` for signup security-question dropdowns and for “forgot password” flows.

---

### 7. POST `/api/auth/register/driver/complete`

**Content-Type:** `multipart/form-data` (not JSON).

**Form fields:**

| Field             | Type   | Required | Notes |
|-------------------|--------|----------|--------|
| `email`           | string | Yes      | Same email used in POST register |
| `licenseImage`    | file   | Optional | Image file (e.g. license photo) |
| `selfieImage`     | file   | Optional | Selfie image |
| `licenseNumber`   | string | No       | |
| `licenseExpiryDate` | string | No     | |
| `address`         | string | No       | |

**Backend rules:**

- User must exist, be **Driver**, and **email verified**.
- If not verified: `400` – “Email must be verified before completing registration”.
- If already onboarded: `400` – “Registration is already complete”.
- Max file size: **10 MB** per file (configurable server-side as `FileUpload:MaxFileSizeBytes`).

**Success (200):**

```json
{
  "success": true,
  "message": "Driver registration completed successfully",
  "data": {
    "licenseImageUrl": "...",
    "selfieImageUrl": "...",
    "licenseNumber": "...",
    "licenseExpiryDate": "...",
    "address": "..."
  }
}
```

**Errors:** `400` (validation / business rules), `500` (server error).

---

## Validation rules

### Password (from Identity options in code)

- Minimum length: **8**
- At least one **digit**
- At least one **lowercase**
- At least one **uppercase**
- At least one **non-alphanumeric** (e.g. `!@#$%`)
- At least **4 unique characters**

Suggest showing these rules on the signup form and validating in the frontend before calling the API.

### Email

- Normalized: lowercase, trim.
- Check-email endpoint treats “no @ or no .” as invalid format.

### Security questions

- If sent: `questionId` must be in **1–20**, and `answer` must be non-empty.
- You can send 1, 2, or 3 questions.

### Roles

- For **main app** registration use only: `Customer` or `Driver`.
- Omit `role` to default to Customer.

---

## Rate limits

- **Register / login:** 15 requests per 5 minutes per client (IP).
- **Check-email:** 10 requests per 5 minutes per client.

On limit exceeded the API returns **429** (or equivalent); frontend should show a “Too many attempts, try again later” message.

---

## Frontend implementation steps

### Customer registration

1. **Optional:** Call `POST /api/auth/check-email` when user leaves the email field (debounced) to show “Email available” / “Email already registered”.
2. **Optional:** Call `GET /api/auth/security-questions` once and cache the list for dropdowns.
3. On submit:
   - Validate email, password (length, digit, upper, lower, non-alphanumeric, unique chars), fullName.
   - Validate security questions if you collect them (questionId 1–20, answer non-empty).
   - `POST /api/auth/register` with `email`, `password`, `fullName`, optional `role: "Customer"`, `phoneNumber`, `referralCode`, `securityQuestion1` / `2` / `3`.
4. On success: show “Check your email to verify your account” and optionally a “Resend verification” button that calls `POST /api/auth/resend-verification`.
5. Verification link in email should open your app with `email` and `token` (e.g. query params). Your app then calls `POST /api/auth/verify-email` with that `email` and `token` and shows “Email verified” and redirect to login.

### Driver registration (two steps)

1. **Step 1 – Account creation (same as customer, with role Driver):**
   - Optionally use check-email and security-questions as above.
   - `POST /api/auth/register` with `role: "Driver"` and same fields as customer.
   - Show “Check your email to verify” and “Resend verification” using resend-verification.

2. **Step 2 – After email verification:**
   - User opens app and enters the same email (or is deep-linked with email).
   - Call `GET /api/auth/registration-status?email=...`:
     - If `canResume === true`: show “Complete your driver registration” form (documents).
     - If `registrationComplete === true`: redirect to login.
     - If `emailVerified === false`: show “Please verify your email first” and resend option.
   - “Complete registration” form: collect `email`, `licenseImage`, `selfieImage`, and optionally `licenseNumber`, `licenseExpiryDate`, `address`. Submit as `multipart/form-data` to `POST /api/auth/register/driver/complete`.
   - On success: show “Registration complete” and redirect to login.

---

## Example: Customer registration

```ts
// 1. Check email (optional, e.g. on blur)
const checkEmail = async (email: string) => {
  const res = await fetch(`${API_BASE}/api/auth/check-email`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: email.trim().toLowerCase() }),
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.message || 'Check failed');
  return data; // { success, available, message?, existingRole? }
};

// 2. Register
const register = async (payload: {
  email: string;
  password: string;
  fullName: string;
  role?: 'Customer' | 'Driver';
  phoneNumber?: string;
  referralCode?: string;
  securityQuestion1?: { questionId: number; answer: string };
  securityQuestion2?: { questionId: number; answer: string };
  securityQuestion3?: { questionId: number; answer: string };
}) => {
  const res = await fetch(`${API_BASE}/api/auth/register`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.message || 'Registration failed');
  return data; // { success, message, requiresEmailVerification, email }
};
```

---

## Example: Driver registration (two steps)

```ts
// Step 1: same as customer with role: 'Driver'
await register({ email, password, fullName, role: 'Driver', ... });

// Step 2: after user verified email – check status
const status = async (email: string) => {
  const res = await fetch(
    `${API_BASE}/api/auth/registration-status?email=${encodeURIComponent(email)}`
  );
  const data = await res.json();
  if (!res.ok) throw new Error(data.message || 'Failed');
  return data; // { success, emailVerified, registrationComplete, canResume }
};

// Step 3: complete with documents
const completeDriverRegistration = async (
  email: string,
  files: { licenseImage?: File; selfieImage?: File },
  optional: { licenseNumber?: string; licenseExpiryDate?: string; address?: string }
) => {
  const form = new FormData();
  form.append('email', email);
  if (files.licenseImage) form.append('licenseImage', files.licenseImage);
  if (files.selfieImage) form.append('selfieImage', files.selfieImage);
  if (optional.licenseNumber) form.append('licenseNumber', optional.licenseNumber);
  if (optional.licenseExpiryDate) form.append('licenseExpiryDate', optional.licenseExpiryDate);
  if (optional.address) form.append('address', optional.address);

  const res = await fetch(`${API_BASE}/api/auth/register/driver/complete`, {
    method: 'POST',
    body: form,
    // Do not set Content-Type; browser sets multipart boundary
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.message || 'Complete registration failed');
  return data;
};
```

---

## Summary

| Goal | Endpoint | When |
|------|----------|------|
| Check if email is free | POST `/api/auth/check-email` | Before/during signup (optional) |
| Create account | POST `/api/auth/register` | Customer or Driver step 1 |
| List security questions | GET `/api/auth/security-questions` | Once for signup/forgot-password UI |
| Verify email (from link) | POST `/api/auth/verify-email` | When user clicks email link |
| Resend verification | POST `/api/auth/resend-verification` | “Resend email” button |
| Driver: can I complete? | GET `/api/auth/registration-status?email=` | After driver verified email |
| Driver: submit documents | POST `/api/auth/register/driver/complete` (form-data) | Driver step 2 |

All request/response shapes and status codes above match the current backend implementation. If the API changes, update this guide and your frontend types accordingly.
