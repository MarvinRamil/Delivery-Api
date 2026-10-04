using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Drivers.Domain;

public enum DriverApplicationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public class DriverApplication : Entity
{
    public string? UserId { get; private set; } // FK to ApplicationUser (nullable for backward compatibility)
    public string FullName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public string FacebookProfileUrl { get; private set; } = string.Empty;

    public string? VehicleType { get; private set; }
    public string? VehiclePlate { get; private set; }
    public string? VehicleModel { get; private set; }
    public string? VehicleColor { get; private set; }

    public string DriversLicensePath { get; private set; } = string.Empty;
    public string ClearancePath { get; private set; } = string.Empty;
    public string OrCrPath { get; private set; } = string.Empty;
    public string LtfrbPaPath { get; private set; } = string.Empty;
    public string InsurancePath { get; private set; } = string.Empty;

    public DriverApplicationStatus Status { get; private set; } = DriverApplicationStatus.Pending;
    public string? Notes { get; private set; }
    public string? ApprovedByUserId { get; private set; }
    public DateTime? ApprovedAt { get; private set; }

    /// <summary>Total number of times this application has been submitted (initial submit + resubmissions).</summary>
    public int SubmissionCount { get; private set; } = 1;

    private DriverApplication() { }

    public DriverApplication(
        string fullName,
        string email,
        string phone,
        string facebookProfileUrl,
        string driversLicensePath,
        string clearancePath,
        string orCrPath,
        string ltfrbPaPath,
        string insurancePath,
        string? userId = null,
        string? vehicleType = null,
        string? vehiclePlate = null,
        string? vehicleModel = null,
        string? vehicleColor = null,
        int submissionCount = 1)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        VehicleType = vehicleType;
        VehiclePlate = vehiclePlate;
        VehicleModel = vehicleModel;
        VehicleColor = vehicleColor;
        FullName = fullName;
        Email = email;
        Phone = phone;
        FacebookProfileUrl = facebookProfileUrl;
        DriversLicensePath = driversLicensePath;
        ClearancePath = clearancePath;
        OrCrPath = orCrPath;
        LtfrbPaPath = ltfrbPaPath;
        InsurancePath = insurancePath;
        Status = DriverApplicationStatus.Pending;
        // Resubmissions create a brand-new application row (new Id) and carry the
        // running attempt count forward, so the back-office keeps a full history
        // of every attempt until the driver is onboarded (approved).
        SubmissionCount = submissionCount < 1 ? 1 : submissionCount;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Sets the stored document paths after the files have been uploaded under
    /// this application's Id (used right after construction on submit/resubmit).
    /// </summary>
    public void SetDocuments(
        string driversLicensePath,
        string clearancePath,
        string orCrPath,
        string ltfrbPaPath,
        string insurancePath)
    {
        DriversLicensePath = driversLicensePath;
        ClearancePath = clearancePath;
        OrCrPath = orCrPath;
        LtfrbPaPath = ltfrbPaPath;
        InsurancePath = insurancePath;
    }

    public void Approve(string approvedByUserId, string? notes = null)
    {
        Status = DriverApplicationStatus.Approved;
        ApprovedByUserId = approvedByUserId;
        ApprovedAt = DateTime.UtcNow;
        Notes = notes;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Reject(string approvedByUserId, string? notes = null)
    {
        Status = DriverApplicationStatus.Rejected;
        ApprovedByUserId = approvedByUserId;
        ApprovedAt = DateTime.UtcNow;
        Notes = notes;
        UpdatedAt = DateTime.UtcNow;
    }

    public void WipeData(string anonymizedName)
    {
        FullName = anonymizedName;
        Email = "deleted_" + Id.ToString("N") + "@bee-app.tech";
        Phone = "00000000000";
        FacebookProfileUrl = "DELETED";
        VehiclePlate = VehiclePlate != null ? "DELETED" : null;
        DriversLicensePath = "DELETED";
        ClearancePath = "DELETED";
        OrCrPath = "DELETED";
        LtfrbPaPath = "DELETED";
        InsurancePath = "DELETED";
        Notes = "Data wiped per Right to Erasure request.";
        UpdatedAt = DateTime.UtcNow;
    }
}

