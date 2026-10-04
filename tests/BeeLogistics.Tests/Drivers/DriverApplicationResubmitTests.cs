using BeeLogistics.Modules.Drivers.Domain;
using Xunit;

namespace BeeLogistics.Tests.Drivers;

public class DriverApplicationResubmitTests
{
    private static DriverApplication NewApplication(int submissionCount = 1)
    {
        return new DriverApplication(
            fullName: "Juan Dela Cruz",
            email: "driver@example.com",
            phone: "+639171234567",
            facebookProfileUrl: "https://facebook.com/juan",
            driversLicensePath: "s3:driver-applications/old-license.webp",
            clearancePath: "s3:driver-applications/old-clearance.webp",
            orCrPath: string.Empty,
            ltfrbPaPath: string.Empty,
            insurancePath: string.Empty,
            userId: "user-1",
            vehicleType: "Motorcycle",
            vehiclePlate: "ABC-1234",
            vehicleModel: "Honda Click",
            vehicleColor: "Red",
            submissionCount: submissionCount);
    }

    [Fact]
    public void New_application_starts_with_submission_count_of_one()
    {
        var application = NewApplication();

        Assert.Equal(1, application.SubmissionCount);
        Assert.Equal(DriverApplicationStatus.Pending, application.Status);
    }

    [Fact]
    public void Resubmission_is_a_new_application_with_a_new_id()
    {
        // A rejected application; the driver resubmits, producing a brand-new row.
        var previous = NewApplication();
        previous.Reject("admin-1", "Plate number does not match OR/CR");

        var resubmission = NewApplication(submissionCount: previous.SubmissionCount + 1);

        Assert.NotEqual(previous.Id, resubmission.Id);
        Assert.Equal(DriverApplicationStatus.Rejected, previous.Status);
        Assert.Equal(DriverApplicationStatus.Pending, resubmission.Status);
    }

    [Fact]
    public void Resubmission_carries_the_running_attempt_count_forward()
    {
        var first = NewApplication();
        Assert.Equal(1, first.SubmissionCount);

        var second = NewApplication(submissionCount: first.SubmissionCount + 1);
        Assert.Equal(2, second.SubmissionCount);

        var third = NewApplication(submissionCount: second.SubmissionCount + 1);
        Assert.Equal(3, third.SubmissionCount);
    }

    [Fact]
    public void Resubmission_starts_pending_with_no_prior_review_outcome()
    {
        var resubmission = NewApplication(submissionCount: 2);

        Assert.Equal(DriverApplicationStatus.Pending, resubmission.Status);
        Assert.Null(resubmission.Notes);
        Assert.Null(resubmission.ApprovedByUserId);
        Assert.Null(resubmission.ApprovedAt);
    }

    [Fact]
    public void Submission_count_below_one_is_clamped_to_one()
    {
        var application = NewApplication(submissionCount: 0);

        Assert.Equal(1, application.SubmissionCount);
    }

    [Fact]
    public void SetDocuments_updates_the_stored_document_paths()
    {
        var application = NewApplication();

        application.SetDocuments(
            driversLicensePath: "s3:driver-applications/new-license.webp",
            clearancePath: "s3:driver-applications/new-clearance.webp",
            orCrPath: "s3:driver-applications/new-orcr.webp",
            ltfrbPaPath: string.Empty,
            insurancePath: string.Empty);

        Assert.Equal("s3:driver-applications/new-license.webp", application.DriversLicensePath);
        Assert.Equal("s3:driver-applications/new-clearance.webp", application.ClearancePath);
        Assert.Equal("s3:driver-applications/new-orcr.webp", application.OrCrPath);
    }

    [Fact]
    public void Email_is_identity_bound_and_preserved_on_the_new_application()
    {
        var resubmission = NewApplication(submissionCount: 2);

        Assert.Equal("driver@example.com", resubmission.Email);
    }
}
