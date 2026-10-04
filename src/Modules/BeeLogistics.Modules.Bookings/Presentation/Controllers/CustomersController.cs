using BeeLogistics.Modules.Bookings.Application.DTOs;
using BeeLogistics.Modules.Bookings.Application.Handlers;
using BeeLogistics.Shared.Presentation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BeeLogistics.Modules.Bookings.Presentation.Controllers;

[Authorize(Policy = "Backoffice")]
public class CustomersController : BaseController
{
    private readonly IMediator _mediator;

    public CustomersController(IMediator mediator) => _mediator = mediator;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => FromResult(await _mediator.Send(new GetCustomersQuery(page, pageSize), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new GetCustomerByIdQuery(id), ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCustomerDto dto, CancellationToken ct)
        => FromResult(await _mediator.Send(new CreateCustomerCommand(dto), ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCustomerDto dto, CancellationToken ct)
        => FromResult(await _mediator.Send(new UpdateCustomerCommand(id, dto), ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => FromResult(await _mediator.Send(new DeleteCustomerCommand(id), ct));
}
