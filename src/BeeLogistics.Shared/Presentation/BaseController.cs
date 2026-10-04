using BeeLogistics.Shared.Abstractions;
using BeeLogistics.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Shared.Presentation;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public abstract class BaseController : ControllerBase
{
    protected IActionResult FromResult(Result result)
    {
        if (result.IsSuccess)
            return Ok(ApiResponse.Ok());

        var error = result.Error ?? "An error occurred";

        if (result.ErrorKind == ResultErrorKind.Forbidden)
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail(error));

        if (IsNotFound(result))
            return NotFound(ApiResponse.Fail(error));

        return BadRequest(ApiResponse.Fail(error));
    }

    protected IActionResult FromResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
            return Ok(ApiResponse<T>.Ok(result.Value!));

        var error = result.Error ?? "An error occurred";

        if (result.ErrorKind == ResultErrorKind.Forbidden)
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<T>.Fail(error));

        if (IsNotFound(result))
            return NotFound(ApiResponse<T>.Fail(error));

        return BadRequest(ApiResponse<T>.Fail(error));
    }

    // The message sniff is the legacy path: plenty of handlers still signal a missing
    // resource with a plain Result.Fail("... not found") rather than Result.NotFound.
    private static bool IsNotFound(Result result)
        => result.ErrorKind == ResultErrorKind.NotFound
           || result.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true;

    protected IActionResult Created<T>(T data, string? routeName = null, object? routeValues = null)
    {
        var response = ApiResponse<T>.Ok(data);
        if (routeName != null)
            return CreatedAtRoute(routeName, routeValues, response);
        return StatusCode(201, response);
    }

    protected IActionResult NoContentResult() => NoContent();

    protected IActionResult PagedResult<T>(PagedResult<T> pagedResult)
        => Ok(ApiResponse<PagedResult<T>>.Ok(pagedResult));
}
