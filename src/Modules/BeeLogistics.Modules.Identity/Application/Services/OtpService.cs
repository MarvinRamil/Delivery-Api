using Microsoft.Extensions.Caching.Distributed;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BeeLogistics.Modules.Identity.Application.Services;

/// <summary>
/// Purpose of the OTP email; used to show dynamic subject and body text.
/// </summary>
public enum OtpPurpose
{
    /// <summary>Email verification for account creation / registration.</summary>
    EmailVerification,
    /// <summary>Password change for existing account.</summary>
    PasswordChange,
    /// <summary>Password reset (forgot password) for mobile apps.</summary>
    PasswordReset
}

/// <summary>
/// Service for generating and validating OTP (One-Time Password) codes
/// </summary>
public class OtpService
{
    private readonly IDistributedCache _cache;
    private const int OtpLength = 6;
    private const int OtpExpirationMinutes = 10;
    private const string OtpCachePrefix = "otp:password-change:";
    private const string EmailVerificationOtpPrefix = "otp:email-verification:";
    private const string PhoneVerificationOtpPrefix = "otp:phone-verification:";
    private const string PhoneVerificationLockPrefix = "otp:phone-verification:lock:";
    private const string PasswordResetOtpPrefix = "otp:password-reset:";
    private const string EmailVerifiedForRegistrationPrefix = "email-verified-for-registration:";
    private const string PhoneVerifiedForRegistrationPrefix = "phone-verified-for-registration:";
    private const int EmailVerifiedForRegistrationMinutes = 20;

    public OtpService(IDistributedCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Generate a 6-digit OTP and store it in cache
    /// </summary>
    public async Task<string> GenerateOtpAsync(string userId, CancellationToken ct = default)
    {
        // Generate random 6-digit OTP
        var otp = RandomNumberGenerator.GetInt32(100000, 999999).ToString("D6");
        
        // Store in cache with expiration
        var cacheKey = $"{OtpCachePrefix}{userId}";
        var otpData = new OtpData
        {
            Code = otp,
            GeneratedAt = DateTime.UtcNow,
            Attempts = 0
        };
        
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OtpExpirationMinutes)
        };
        
        var json = JsonSerializer.Serialize(otpData);
        await _cache.SetStringAsync(cacheKey, json, options, ct);
        
        return otp;
    }

    /// <summary>
    /// Validate OTP code
    /// </summary>
    public async Task<OtpValidationResult> ValidateOtpAsync(string userId, string otp, CancellationToken ct = default)
    {
        var cacheKey = $"{OtpCachePrefix}{userId}";
        var cachedJson = await _cache.GetStringAsync(cacheKey, ct);
        
        if (string.IsNullOrEmpty(cachedJson))
        {
            return new OtpValidationResult(false, "OTP not found or expired. Please request a new OTP.");
        }

        var otpData = JsonSerializer.Deserialize<OtpData>(cachedJson);
        if (otpData == null)
        {
            return new OtpValidationResult(false, "Invalid OTP data.");
        }

        // Check if OTP has expired
        if (DateTime.UtcNow > otpData.GeneratedAt.AddMinutes(OtpExpirationMinutes))
        {
            await _cache.RemoveAsync(cacheKey, ct);
            return new OtpValidationResult(false, "OTP has expired. Please request a new OTP.");
        }

        // Check max attempts (prevent brute force)
        if (otpData.Attempts >= 5)
        {
            await _cache.RemoveAsync(cacheKey, ct);
            return new OtpValidationResult(false, "Maximum attempts exceeded. Please request a new OTP.");
        }

        // Increment attempts
        otpData.Attempts++;
        var updatedJson = JsonSerializer.Serialize(otpData);
        await _cache.SetStringAsync(cacheKey, updatedJson, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OtpExpirationMinutes)
        }, ct);

        // SECURITY: Use constant-time comparison to prevent timing attacks (OWASP Top 10 - A07:2021)
        if (!ConstantTimeEquals(otpData.Code, otp))
        {
            return new OtpValidationResult(false, $"Invalid OTP. {5 - otpData.Attempts} attempts remaining.");
        }

        // OTP is valid - remove from cache
        await _cache.RemoveAsync(cacheKey, ct);
        return new OtpValidationResult(true, "OTP validated successfully.");
    }

    /// <summary>
    /// Send OTP via email. Subject and body text are dynamic based on <paramref name="purpose"/>.
    /// </summary>
    /// <param name="fullName">Recipient display name (e.g. user name or email prefix).</param>
    /// <param name="otp">The 6-digit OTP code.</param>
    /// <param name="purpose">Purpose of the OTP (e.g. EmailVerification for sign-up, PasswordChange for password reset).</param>
    public static string CreateOtpEmailHtml(string fullName, string otp, OtpPurpose purpose = OtpPurpose.PasswordChange)
    {
        const string AppName = "My Bee App On-Demand";
        var template = LoadOtpTemplate();
        var copy = GetOtpPurposeCopy(purpose);

        var logoBase64 = GetLogoBase64();
        var logoImg = !string.IsNullOrEmpty(logoBase64)
            ? $@"<img src=""data:image/png;base64,{logoBase64}"" alt=""MyBeeApp Logo"" style=""max-width: 180px; height: auto; display: block; margin: 0 auto;"" />"
            : @"<h1 style=""margin: 0; color: #1c190d; font-size: 32px; font-weight: 700; letter-spacing: -0.5px;"">MyBeeApp</h1>";

        var placeholders = new Dictionary<string, string>
        {
            { "AppName", AppName },
            { "FullName", fullName },
            { "OTP", otp },
            { "Year", DateTime.UtcNow.Year.ToString() },
            { "Logo", logoImg },
            { "PageTitle", copy.PageTitle },
            { "SubjectTitle", copy.SubjectTitle },
            { "IntroLine", copy.IntroLine },
            { "FooterLine", copy.FooterLine }
        };

        return ReplacePlaceholders(template, placeholders);
    }

    /// <summary>
    /// Returns the email subject line for the given OTP purpose (e.g. for use in EmailMessage.Subject).
    /// </summary>
    public static string GetOtpEmailSubject(OtpPurpose purpose, string appName = "My Bee App On-Demand")
    {
        var title = purpose switch
        {
            OtpPurpose.EmailVerification => "Email Verification Code",
            OtpPurpose.PasswordChange => "Password Change Verification Code",
            OtpPurpose.PasswordReset => "Password Reset Verification Code",
            _ => "Verification Code"
        };
        return $"{title} - {appName}";
    }

    private static (string PageTitle, string SubjectTitle, string IntroLine, string FooterLine) GetOtpPurposeCopy(OtpPurpose purpose)
    {
        return purpose switch
        {
            OtpPurpose.EmailVerification => (
                PageTitle: "Email Verification Code",
                SubjectTitle: "Email Verification Code",
                IntroLine: "You requested to verify your email to create an account. Use the verification code below to continue:",
                FooterLine: "If you didn't request this code, please ignore this email and contact support if you have concerns."
            ),
            OtpPurpose.PasswordChange => (
                PageTitle: "Password Change Verification Code",
                SubjectTitle: "Password Change Verification Code",
                IntroLine: "You requested to change your password. Use the verification code below to complete the process:",
                FooterLine: "If you didn't request a password change, please ignore this email and contact support immediately."
            ),
            OtpPurpose.PasswordReset => (
                PageTitle: "Password Reset Verification Code",
                SubjectTitle: "Password Reset Verification Code",
                IntroLine: "You requested to reset your password. Use the verification code below to complete the process:",
                FooterLine: "If you didn't request a password reset, please ignore this email and contact support immediately."
            ),
            _ => GetOtpPurposeCopy(OtpPurpose.PasswordChange)
        };
    }

    /// <summary>
    /// Load OTP template from embedded resource
    /// </summary>
    private static string LoadOtpTemplate()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = "BeeLogistics.Modules.Identity.Templates.Email.otp-email.html";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"OTP template not found as embedded resource '{resourceName}'");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load OTP email template: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Replace placeholders in template
    /// </summary>
    private static string ReplacePlaceholders(string template, Dictionary<string, string> placeholders)
    {
        var result = template;
        foreach (var (key, value) in placeholders)
        {
            result = result.Replace($"{{{key}}}", value);
        }
        return result;
    }

    private static string GetLogoBase64()
    {
        try
        {
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Modules", "BeeLogistics.Modules.Identity", "assets", "bee_logo.png"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "bee_logo.png"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "Modules", "BeeLogistics.Modules.Identity", "assets", "bee_logo.png"),
                Path.Combine(Directory.GetCurrentDirectory(), "assets", "bee_logo.png")
            };

            foreach (var path in possiblePaths)
            {
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    var imageBytes = File.ReadAllBytes(fullPath);
                    return Convert.ToBase64String(imageBytes);
                }
            }
        }
        catch
        {
            // If logo can't be loaded, return empty string
        }

        return string.Empty;
    }

    /// <summary>
    /// Generate OTP for email verification (before account creation)
    /// Uses email as key instead of userId
    /// </summary>
    public async Task<string> GenerateEmailVerificationOtpAsync(string email, CancellationToken ct = default)
    {
        // Normalize email (lowercase, trim)
        var normalizedEmail = email.Trim().ToLowerInvariant();
        
        // Generate random 6-digit OTP
        var otp = RandomNumberGenerator.GetInt32(100000, 999999).ToString("D6");
        
        // Store in cache with expiration
        var cacheKey = $"{EmailVerificationOtpPrefix}{normalizedEmail}";
        var otpData = new OtpData
        {
            Code = otp,
            GeneratedAt = DateTime.UtcNow,
            Attempts = 0
        };
        
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OtpExpirationMinutes)
        };
        
        var json = JsonSerializer.Serialize(otpData);
        await _cache.SetStringAsync(cacheKey, json, options, ct);
        
        return otp;
    }

    /// <summary>
    /// Validate email verification OTP
    /// </summary>
    public async Task<OtpValidationResult> ValidateEmailVerificationOtpAsync(string email, string otp, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var cacheKey = $"{EmailVerificationOtpPrefix}{normalizedEmail}";
        var cachedJson = await _cache.GetStringAsync(cacheKey, ct);
        
        if (string.IsNullOrEmpty(cachedJson))
        {
            return new OtpValidationResult(false, "OTP not found or expired. Please request a new OTP.");
        }

        var otpData = JsonSerializer.Deserialize<OtpData>(cachedJson);
        if (otpData == null)
        {
            return new OtpValidationResult(false, "Invalid OTP data.");
        }

        // Check if OTP has expired
        if (DateTime.UtcNow > otpData.GeneratedAt.AddMinutes(OtpExpirationMinutes))
        {
            await _cache.RemoveAsync(cacheKey, ct);
            return new OtpValidationResult(false, "OTP has expired. Please request a new OTP.");
        }

        // Check max attempts (prevent brute force)
        if (otpData.Attempts >= 5)
        {
            await _cache.RemoveAsync(cacheKey, ct);
            // Lock email for 30 minutes after 5 failed attempts
            var lockKey = $"{EmailVerificationOtpPrefix}lock:{normalizedEmail}";
            await _cache.SetStringAsync(lockKey, "locked", new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            }, ct);
            return new OtpValidationResult(false, "Maximum attempts exceeded. Email locked for 30 minutes. Please try again later.");
        }

        // Check if email is locked
        var lockKeyCheck = $"{EmailVerificationOtpPrefix}lock:{normalizedEmail}";
        var isLocked = await _cache.GetStringAsync(lockKeyCheck, ct);
        if (!string.IsNullOrEmpty(isLocked))
        {
            return new OtpValidationResult(false, "Email is temporarily locked due to too many failed attempts. Please try again later.");
        }

        // Increment attempts
        otpData.Attempts++;
        var updatedJson = JsonSerializer.Serialize(otpData);
        await _cache.SetStringAsync(cacheKey, updatedJson, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OtpExpirationMinutes)
        }, ct);

        // SECURITY: Use constant-time comparison to prevent timing attacks (OWASP Top 10 - A07:2021)
        if (!ConstantTimeEquals(otpData.Code, otp))
        {
            return new OtpValidationResult(false, $"Invalid OTP. {5 - otpData.Attempts} attempts remaining.");
        }

        // OTP is valid - remove from cache
        await _cache.RemoveAsync(cacheKey, ct);
        return new OtpValidationResult(true, "OTP validated successfully.");
    }

    /// <summary>
    /// Check if email is locked (too many failed OTP attempts)
    /// </summary>
    public async Task<bool> IsEmailLockedAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var lockKey = $"{EmailVerificationOtpPrefix}lock:{normalizedEmail}";
        var isLocked = await _cache.GetStringAsync(lockKey, ct);
        return !string.IsNullOrEmpty(isLocked);
    }

    /// <summary>
    /// Generate OTP for phone verification (before account creation)
    /// Uses phone number as key instead of userId
    /// </summary>
    public async Task<string> GeneratePhoneVerificationOtpAsync(string phoneNumber, CancellationToken ct = default)
    {
        var normalizedPhone = phoneNumber.Trim();

        // Generate random 6-digit OTP
        var otp = RandomNumberGenerator.GetInt32(100000, 999999).ToString("D6");

        // Store in cache with expiration
        var cacheKey = $"{PhoneVerificationOtpPrefix}{normalizedPhone}";
        var otpData = new OtpData
        {
            Code = otp,
            GeneratedAt = DateTime.UtcNow,
            Attempts = 0
        };

        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OtpExpirationMinutes)
        };

        var json = JsonSerializer.Serialize(otpData);
        await _cache.SetStringAsync(cacheKey, json, options, ct);

        return otp;
    }

    /// <summary>
    /// Validate phone verification OTP
    /// </summary>
    public async Task<OtpValidationResult> ValidatePhoneVerificationOtpAsync(string phoneNumber, string otp, CancellationToken ct = default)
    {
        var normalizedPhone = phoneNumber.Trim();
        var cacheKey = $"{PhoneVerificationOtpPrefix}{normalizedPhone}";
        var cachedJson = await _cache.GetStringAsync(cacheKey, ct);

        if (string.IsNullOrEmpty(cachedJson))
        {
            return new OtpValidationResult(false, "OTP not found or expired. Please request a new OTP.");
        }

        var otpData = JsonSerializer.Deserialize<OtpData>(cachedJson);
        if (otpData == null)
        {
            return new OtpValidationResult(false, "Invalid OTP data.");
        }

        // Check if OTP has expired
        if (DateTime.UtcNow > otpData.GeneratedAt.AddMinutes(OtpExpirationMinutes))
        {
            await _cache.RemoveAsync(cacheKey, ct);
            return new OtpValidationResult(false, "OTP has expired. Please request a new OTP.");
        }

        // Check max attempts (prevent brute force)
        if (otpData.Attempts >= 5)
        {
            await _cache.RemoveAsync(cacheKey, ct);
            // Lock phone for 30 minutes after 5 failed attempts
            var lockKey = $"{PhoneVerificationLockPrefix}{normalizedPhone}";
            await _cache.SetStringAsync(lockKey, "locked", new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            }, ct);
            return new OtpValidationResult(false, "Maximum attempts exceeded. Phone number locked for 30 minutes. Please try again later.");
        }

        // Check if phone is locked
        var lockKeyCheck = $"{PhoneVerificationLockPrefix}{normalizedPhone}";
        var isLocked = await _cache.GetStringAsync(lockKeyCheck, ct);
        if (!string.IsNullOrEmpty(isLocked))
        {
            return new OtpValidationResult(false, "Phone number is temporarily locked due to too many failed attempts. Please try again later.");
        }

        // Increment attempts
        otpData.Attempts++;
        var updatedJson = JsonSerializer.Serialize(otpData);
        await _cache.SetStringAsync(cacheKey, updatedJson, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OtpExpirationMinutes)
        }, ct);

        // SECURITY: Use constant-time comparison to prevent timing attacks (OWASP Top 10 - A07:2021)
        if (!ConstantTimeEquals(otpData.Code, otp))
        {
            return new OtpValidationResult(false, $"Invalid OTP. {5 - otpData.Attempts} attempts remaining.");
        }

        // OTP is valid - remove from cache
        await _cache.RemoveAsync(cacheKey, ct);
        return new OtpValidationResult(true, "OTP validated successfully.");
    }

    /// <summary>
    /// Check if phone is locked (too many failed OTP attempts)
    /// </summary>
    public async Task<bool> IsPhoneLockedAsync(string phoneNumber, CancellationToken ct = default)
    {
        var normalizedPhone = phoneNumber.Trim();
        var lockKey = $"{PhoneVerificationLockPrefix}{normalizedPhone}";
        var isLocked = await _cache.GetStringAsync(lockKey, ct);
        return !string.IsNullOrEmpty(isLocked);
    }

    /// <summary>
    /// Invalidate existing OTP for phone (used when resending)
    /// </summary>
    public async Task InvalidatePhoneVerificationOtpAsync(string phoneNumber, CancellationToken ct = default)
    {
        var normalizedPhone = phoneNumber.Trim();
        var cacheKey = $"{PhoneVerificationOtpPrefix}{normalizedPhone}";
        await _cache.RemoveAsync(cacheKey, ct);
    }

    /// <summary>
    /// Invalidate existing OTP for email (used when resending)
    /// </summary>
    public async Task InvalidateEmailVerificationOtpAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var cacheKey = $"{EmailVerificationOtpPrefix}{normalizedEmail}";
        await _cache.RemoveAsync(cacheKey, ct);
    }

    /// <summary>
    /// Mark email as verified for registration (one-time use). Call after successful OTP validation.
    /// Register endpoint will consume this and set EmailConfirmed = true.
    /// </summary>
    public async Task SetEmailVerifiedForRegistrationAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var cacheKey = $"{EmailVerifiedForRegistrationPrefix}{normalizedEmail}";
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(EmailVerifiedForRegistrationMinutes)
        };
        await _cache.SetStringAsync(cacheKey, "1", options, ct);
    }

    /// <summary>
    /// Consume "email verified for registration" marker (one-time). Returns true if email was verified via OTP.
    /// Register calls this; if true, create user with EmailConfirmed = true and do not send verification email.
    /// </summary>
    public async Task<bool> ConsumeEmailVerifiedForRegistrationAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var cacheKey = $"{EmailVerifiedForRegistrationPrefix}{normalizedEmail}";
        var value = await _cache.GetStringAsync(cacheKey, ct);
        if (string.IsNullOrEmpty(value))
            return false;
        await _cache.RemoveAsync(cacheKey, ct);
        return true;
    }

    /// <summary>
    /// Mark phone number as verified for registration (one-time use). Call after successful OTP validation.
    /// Register-by-phone endpoint will consume this and set PhoneNumberConfirmed = true.
    /// </summary>
    public async Task SetPhoneVerifiedForRegistrationAsync(string phoneNumber, CancellationToken ct = default)
    {
        var normalizedPhone = phoneNumber.Trim();
        var cacheKey = $"{PhoneVerifiedForRegistrationPrefix}{normalizedPhone}";
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(EmailVerifiedForRegistrationMinutes)
        };
        await _cache.SetStringAsync(cacheKey, "1", options, ct);
    }

    /// <summary>
    /// Consume \"phone verified for registration\" marker (one-time). Returns true if phone was verified via OTP.
    /// Register-by-phone calls this; if true, create user with PhoneNumberConfirmed = true.
    /// </summary>
    public async Task<bool> ConsumePhoneVerifiedForRegistrationAsync(string phoneNumber, CancellationToken ct = default)
    {
        var normalizedPhone = phoneNumber.Trim();
        var cacheKey = $"{PhoneVerifiedForRegistrationPrefix}{normalizedPhone}";
        var value = await _cache.GetStringAsync(cacheKey, ct);
        if (string.IsNullOrEmpty(value))
            return false;
        await _cache.RemoveAsync(cacheKey, ct);
        return true;
    }

    /// <summary>
    /// Generate OTP for password reset (forgot password) - uses email as key
    /// </summary>
    public async Task<string> GeneratePasswordResetOtpAsync(string email, CancellationToken ct = default)
    {
        // Normalize email (lowercase, trim)
        var normalizedEmail = email.Trim().ToLowerInvariant();
        
        // Generate random 6-digit OTP
        var otp = RandomNumberGenerator.GetInt32(100000, 999999).ToString("D6");
        
        // Store in cache with expiration
        var cacheKey = $"{PasswordResetOtpPrefix}{normalizedEmail}";
        var otpData = new OtpData
        {
            Code = otp,
            GeneratedAt = DateTime.UtcNow,
            Attempts = 0
        };
        
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OtpExpirationMinutes)
        };
        
        var json = JsonSerializer.Serialize(otpData);
        await _cache.SetStringAsync(cacheKey, json, options, ct);
        
        return otp;
    }

    /// <summary>
    /// Validate password reset OTP
    /// </summary>
    public async Task<OtpValidationResult> ValidatePasswordResetOtpAsync(string email, string otp, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var cacheKey = $"{PasswordResetOtpPrefix}{normalizedEmail}";
        var cachedJson = await _cache.GetStringAsync(cacheKey, ct);
        
        if (string.IsNullOrEmpty(cachedJson))
        {
            return new OtpValidationResult(false, "OTP not found or expired. Please request a new OTP.");
        }

        var otpData = JsonSerializer.Deserialize<OtpData>(cachedJson);
        if (otpData == null)
        {
            return new OtpValidationResult(false, "Invalid OTP data.");
        }

        // Check if OTP has expired
        if (DateTime.UtcNow > otpData.GeneratedAt.AddMinutes(OtpExpirationMinutes))
        {
            await _cache.RemoveAsync(cacheKey, ct);
            return new OtpValidationResult(false, "OTP has expired. Please request a new OTP.");
        }

        // Check max attempts (prevent brute force)
        if (otpData.Attempts >= 5)
        {
            await _cache.RemoveAsync(cacheKey, ct);
            return new OtpValidationResult(false, "Maximum attempts exceeded. Please request a new OTP.");
        }

        // Increment attempts
        otpData.Attempts++;
        var updatedJson = JsonSerializer.Serialize(otpData);
        await _cache.SetStringAsync(cacheKey, updatedJson, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OtpExpirationMinutes)
        }, ct);

        // SECURITY: Use constant-time comparison to prevent timing attacks (OWASP Top 10 - A07:2021)
        if (!ConstantTimeEquals(otpData.Code, otp))
        {
            return new OtpValidationResult(false, $"Invalid OTP. {5 - otpData.Attempts} attempts remaining.");
        }

        // OTP is valid - remove from cache
        await _cache.RemoveAsync(cacheKey, ct);
        return new OtpValidationResult(true, "OTP validated successfully.");
    }

    /// <summary>
    /// Invalidate existing password reset OTP (used when resending)
    /// </summary>
    public async Task InvalidatePasswordResetOtpAsync(string email, CancellationToken ct = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var cacheKey = $"{PasswordResetOtpPrefix}{normalizedEmail}";
        await _cache.RemoveAsync(cacheKey, ct);
    }
    
    /// <summary>
    /// SECURITY: Constant-time string comparison to prevent timing attacks
    /// Compares two strings in constant time regardless of where differences occur
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a == null || b == null) return a == b;
        if (a.Length != b.Length) return false;
        
        int result = 0;
        for (int i = 0; i < a.Length; i++)
        {
            result |= a[i] ^ b[i];
        }
        return result == 0;
    }
}

/// <summary>
/// OTP data stored in cache
/// </summary>
internal class OtpData
{
    public string Code { get; set; } = null!;
    public DateTime GeneratedAt { get; set; }
    public int Attempts { get; set; }
}

/// <summary>
/// Result of OTP validation
/// </summary>
public record OtpValidationResult(bool IsValid, string Message);

