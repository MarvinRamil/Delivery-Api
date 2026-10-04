// Request/response contracts for AuthController.
//
// Moved out of AuthController.cs unchanged (issue #45). The namespace is deliberately kept as
// ...Presentation.Controllers rather than matching the folder, so that not a single call site,
// using directive or serialized shape changes as a result of the move.

namespace BeeLogistics.Modules.Identity.Presentation.Controllers;

public record LoginRequest(string Email, string Password);
public record SecurityQuestionRequest(int? QuestionId, string? Answer);
public record SendOtpRequest(string Email);
public record VerifyOtpRequest(string Email, string Otp);
public record VerifyOtpAndRegisterRequest(string Email, string Otp, string Password, string FullName, string? Role = null, string? ReferralCode = null, string? DeviceId = null, string? DeviceFingerprint = null);
public record ResendOtpRequest(string Email);
public record SendSmsOtpRequest(string PhoneNumber);
public record VerifySmsOtpRequest(string PhoneNumber, string Otp);
public record ResendSmsOtpRequest(string PhoneNumber);
// CompanyId is accepted and ignored since #43 removed tenancy; kept so older clients that
// still send it do not fail model binding. Drop once the client cleanup has shipped.
public record RegisterRequest(string Email, string Password, string FullName, string? Role = null, Guid? CompanyId = null, string? ReferralCode = null, string? PhoneNumber = null, string? DeviceId = null, string? DeviceFingerprint = null, string? RegistrationToken = null, SecurityQuestionRequest? SecurityQuestion1 = null, SecurityQuestionRequest? SecurityQuestion2 = null, SecurityQuestionRequest? SecurityQuestion3 = null);
public record RegisterByPhoneRequest(string PhoneNumber, string Password, string FullName, string? Role = null, string? RegistrationToken = null, string? DeviceId = null, string? DeviceFingerprint = null, string? ReferralCode = null, SecurityQuestionRequest? SecurityQuestion1 = null, SecurityQuestionRequest? SecurityQuestion2 = null, SecurityQuestionRequest? SecurityQuestion3 = null);
// Login response with optional refresh token for backoffice
public record LoginResponse(string Token, DateTime Expiration, string Role, UserInfo? User = null, string? RefreshToken = null, DateTime? RefreshTokenExpiration = null);
// TenantId, BusinessType and IsSoloDriver no longer have any storage behind them - #43 removed
// tenancy - and are always serialized as null/null/false by BuildUserInfoAsync. They stay on the
// wire because bee-driver is an Expo build: old installs branch on `tenantId !== null`, so making
// the field disappear flips that branch on. Drop them only after the client cleanup has shipped
// and old builds have aged out.
public record UserInfo(string Id, string Email, string FullName, string Role, Guid? TenantId, bool IsOnboarded = false, string? BusinessType = null, bool IsSoloDriver = false, string? ProfilePictureUrl = null, DateTime? LivenessVerifiedAt = null, string? VehiclePlate = null, string? VehicleModel = null, string? VehicleColor = null, string? VehicleType = null);
// SECURITY: Minimal response for backoffice users - only what's needed for UI display
// No user ID, tenant ID, or other internal identifiers exposed
public record BackofficeUserInfo(string FullName, string Email, string Role);
// VehicleType is intentionally absent: it is set only on driver-application approval,
// validated against the vehicle pricing table. Older clients still send it; unknown JSON
// properties are ignored by System.Text.Json, so those requests keep succeeding.
public record UpdateProfileRequest(string? FullName, string? VehiclePlate = null, string? VehicleModel = null, string? VehicleColor = null);
// Type is intentionally absent, for the same reason as UpdateProfileRequest.
public record UpsertDriverVehicleRequest(string PlateNumber, string? Model = null, string? Color = null, bool? IsPrimary = null);
public record DriverVehicleDto(Guid VehicleId, string PlateNumber, string? Model, string? Color, string? Type, bool IsPrimary, DateTime AssignedAt);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword, string? Otp = null);
public record CreateBackofficeUserRequest(string Email, string Password, string FullName, string Role = "Admin");
public record VerifyEmailRequest(string Email, string Token);
public record ResendVerificationRequest(string Email);
public record SecurityAnswerRequest(int? QuestionNumber, string? Answer);
public record ForgotPasswordRequest(string Email, List<SecurityAnswerRequest>? SecurityAnswers = null);
public record ResetPasswordRequest(string Email, string? Token = null, string? Otp = null, string NewPassword = "", List<SecurityAnswerRequest>? SecurityAnswers = null);
public record ResetPasswordMobileRequest(string Email, string Otp, string NewPassword, List<SecurityAnswerRequest>? SecurityAnswers = null);
public record CheckEmailRequest(string Email);
public record RefreshTokenRequest(string RefreshToken, string? DeviceId = null, string? DeviceFingerprint = null);
public record RefreshTokenResponse(string Token, DateTime Expiration, string? RefreshToken = null, DateTime? RefreshTokenExpiration = null);
