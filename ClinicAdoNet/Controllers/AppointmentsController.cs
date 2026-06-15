using ClinicAdoNet.DTOs;
using ClinicAdoNet.Models;
using ClinicAdoNet.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClinicAdoNet.Controllers;

[ApiController]
[Route("api/appointments")]
public class AppointmentsController : ControllerBase
{
    private readonly AppointmentService _appointmentService;

    public AppointmentsController(AppointmentService appointmentService)
    {
        _appointmentService = appointmentService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName,
        [FromQuery] int? idDoctor)
    {
        var result = await _appointmentService.GetAppointmentsAsync(status, patientLastName, idDoctor);

        if (!result.IsSuccess)
        {
            return Error(result);
        }

        return Ok(result.Data);
    }

    [HttpGet("{idAppointment:int}")]
    public async Task<IActionResult> GetAppointment(int idAppointment)
    {
        var appointment = await _appointmentService.GetAppointmentDetailsAsync(idAppointment);

        if (appointment is null)
        {
            return NotFound(new ErrorResponseDto
            {
                Message = "Appointment was not found."
            });
        }

        return Ok(appointment);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment(CreateAppointmentRequestDto request)
    {
        var result = await _appointmentService.CreateAppointmentAsync(request);

        if (!result.IsSuccess)
        {
            return Error(result);
        }

        return CreatedAtAction(
            nameof(GetAppointment),
            new { idAppointment = result.Data!.IdAppointment },
            result.Data);
    }

    [HttpPut("{idAppointment:int}")]
    public async Task<IActionResult> UpdateAppointment(
        int idAppointment,
        UpdateAppointmentRequestDto request)
    {
        var result = await _appointmentService.UpdateAppointmentAsync(idAppointment, request);

        if (!result.IsSuccess)
        {
            return Error(result);
        }

        return Ok(result.Data);
    }

    [HttpDelete("{idAppointment:int}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        var result = await _appointmentService.DeleteAppointmentAsync(idAppointment);

        if (!result.IsSuccess)
        {
            return Error(result);
        }

        return NoContent();
    }

    private IActionResult Error<T>(ServiceResult<T> result)
    {
        var response = new ErrorResponseDto
        {
            Message = result.Message,
            Errors = result.Errors
        };

        return StatusCode(result.StatusCode, response);
    }
}