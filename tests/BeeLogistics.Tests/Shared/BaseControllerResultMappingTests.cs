using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.Presentation;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BeeLogistics.Tests.Shared;

/// <summary>
/// Handler denials must surface as 403, not 400. Before issue #22 every Result.Fail mapped to
/// BadRequest, so an authorization failure was indistinguishable from a validation failure.
/// </summary>
public class BaseControllerResultMappingTests
{
    /// <summary>FromResult is protected; this exposes both overloads for assertion.</summary>
    private sealed class TestController : BaseController
    {
        public IActionResult Map(Result result) => FromResult(result);
        public IActionResult Map<T>(Result<T> result) => FromResult(result);
    }

    private readonly TestController _controller = new();

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 0,
        StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
        _ => 0
    };

    [Fact]
    public void Forbidden_maps_to_403()
    {
        Assert.Equal(403, StatusOf(_controller.Map(Result.Forbidden("nope"))));
        Assert.Equal(403, StatusOf(_controller.Map(Result.Forbidden<string>("nope"))));
    }

    [Fact]
    public void NotFound_maps_to_404()
    {
        Assert.Equal(404, StatusOf(_controller.Map(Result.NotFound("Booking not found"))));
        Assert.Equal(404, StatusOf(_controller.Map(Result.NotFound<string>("Booking not found"))));
    }

    [Fact]
    public void A_plain_failure_still_maps_to_400()
    {
        Assert.Equal(400, StatusOf(_controller.Map(Result.Fail("Cannot cancel a completed booking"))));
        Assert.Equal(400, StatusOf(_controller.Map(Result.Fail<string>("Cannot cancel a completed booking"))));
    }

    [Fact]
    public void A_failure_whose_message_says_not_found_still_maps_to_404()
    {
        // Legacy path: plenty of handlers signal a missing resource with a plain Fail.
        Assert.Equal(404, StatusOf(_controller.Map(Result.Fail("Booking not found"))));
        Assert.Equal(404, StatusOf(_controller.Map(Result.Fail<string>("Booking not found"))));
    }

    [Fact]
    public void Success_maps_to_200()
    {
        Assert.Equal(200, StatusOf(_controller.Map(Result.Ok())));
        Assert.Equal(200, StatusOf(_controller.Map(Result.Ok("payload"))));
    }
}
