using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;

namespace BeeLogistics.Modules.Identity.Domain;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = null!;

    // Clerk identity link. Set when a user is provisioned/migrated to Clerk.
    // Nullable during the migration window; the local row remains the source of
    // truth for Role and contact info, resolved from the Clerk JWT `sub` claim.
    public string? ClerkUserId { get; set; }

    public string Role { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Terms acceptance. Nullable so pre-existing rows stay NULL ("unknown" - they were created
    // before acceptance was recorded); every newly constructed user defaults to true, which
    // covers all creation paths without each call site having to remember to set it.
    public bool? AcceptedTerms { get; set; } = true;
    
    // GPS location for drivers (updated from mobile app)
    public decimal? CurrentLatitude { get; set; }
    public decimal? CurrentLongitude { get; set; }
    public DateTime? LocationUpdatedAt { get; set; }
    
    // Onboarding status
    public bool IsOnboarded { get; set; } = false;
    
    // Driver online status (indicates if driver is available/online)
    public bool IsOnline { get; set; } = false;
    
    // Security questions for password recovery (user can set up to 3)
    public int? SecurityQuestionId1 { get; set; }
    public string? SecurityAnswerHash1 { get; set; } // Hashed answer for security
    
    public int? SecurityQuestionId2 { get; set; }
    public string? SecurityAnswerHash2 { get; set; }
    
    public int? SecurityQuestionId3 { get; set; }
    public string? SecurityAnswerHash3 { get; set; }
    
    // Profile picture URL (stored in S3)
    public string? ProfilePictureUrl { get; set; }

    // Face liveness verification (onboarding)
    public DateTime? LivenessVerifiedAt { get; set; }

    // Last passed per-shift face check (gates going online when ShiftCheck is enabled)
    public DateTime? LastFaceCheckAt { get; set; }

    // Driver vehicle info (optional; for display on bookings)
    public string? VehiclePlate { get; set; }
    public string? VehicleModel { get; set; }
    public string? VehicleColor { get; set; }
    public string? VehicleType { get; set; }

    public ICollection<DriverVehicleAssignment> DriverVehicleAssignments { get; set; } = new List<DriverVehicleAssignment>();
}
