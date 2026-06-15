using ClinicAdoNet.DTOs;
using ClinicAdoNet.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using System.Data;
using ClinicEntityState = ClinicAdoNet.Models.EntityState;

namespace ClinicAdoNet.Services;

public class AppointmentService
{
    private static readonly string[] AllowedStatuses = { "Scheduled", "Completed", "Cancelled" };
    private readonly string _connectionString;

    public AppointmentService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Missing connection string DefaultConnection.");
    }

    public async Task<ServiceResult<List<AppointmentListDto>>> GetAppointmentsAsync(
        string? status,
        string? patientLastName,
        int? idDoctor)
    {
        var normalizedStatus = NormalizeStatus(status);

        if (!string.IsNullOrWhiteSpace(status) && normalizedStatus is null)
        {
            return ServiceResult<List<AppointmentListDto>>.Failure(
                StatusCodes.Status400BadRequest,
                "Invalid query parameters.",
                new List<string> { "Status must be one of: Scheduled, Completed, Cancelled." });
        }

        if (idDoctor is <= 0)
        {
            return ServiceResult<List<AppointmentListDto>>.Failure(
                StatusCodes.Status400BadRequest,
                "Invalid query parameters.",
                new List<string> { "Doctor id must be greater than zero." });
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var command = new SqlCommand("""
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
              AND (@IdDoctor IS NULL OR a.IdDoctor = @IdDoctor)
            ORDER BY a.AppointmentDate;
            """, connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = (object?)normalizedStatus ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80).Value =
            string.IsNullOrWhiteSpace(patientLastName) ? DBNull.Value : patientLastName.Trim();
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = (object?)idDoctor ?? DBNull.Value;

        var appointments = new List<AppointmentListDto>();

        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Reason = reader.GetString(reader.GetOrdinal("Reason")),
                PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail"))
            });
        }

        return ServiceResult<List<AppointmentListDto>>.Success(appointments);
    }

    public async Task<AppointmentDetailsDto?> GetAppointmentDetailsAsync(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        return await GetAppointmentDetailsAsync(connection, idAppointment);
    }

    public async Task<ServiceResult<AppointmentDetailsDto>> CreateAppointmentAsync(CreateAppointmentRequestDto request)
    {
        var validationErrors = ValidateCreateRequest(request);

        if (validationErrors.Count > 0)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status400BadRequest,
                "Invalid appointment data.",
                validationErrors);
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var patientState = await GetPatientStateAsync(connection, request.IdPatient);
        var doctorState = await GetDoctorStateAsync(connection, request.IdDoctor);
        var stateError = GetStateError(patientState, doctorState);

        if (stateError is not null)
        {
            return stateError;
        }

        var hasConflict = await HasDoctorConflictAsync(connection, request.IdDoctor, request.AppointmentDate, null);

        if (hasConflict)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status409Conflict,
                "Doctor already has a scheduled appointment at this time.");
        }

        using var command = new SqlCommand("""
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, N'Scheduled', @Reason);
            """, connection);

        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason.Trim();

        var newId = Convert.ToInt32(await command.ExecuteScalarAsync());
        var createdAppointment = await GetAppointmentDetailsAsync(connection, newId);

        return ServiceResult<AppointmentDetailsDto>.Success(createdAppointment!, StatusCodes.Status201Created);
    }

    public async Task<ServiceResult<AppointmentDetailsDto>> UpdateAppointmentAsync(
        int idAppointment,
        UpdateAppointmentRequestDto request)
    {
        var normalizedStatus = NormalizeStatus(request.Status);
        var validationErrors = ValidateUpdateRequest(request, normalizedStatus);

        if (validationErrors.Count > 0)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status400BadRequest,
                "Invalid appointment data.",
                validationErrors);
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var currentAppointment = await GetAppointmentStateAsync(connection, idAppointment);

        if (currentAppointment is null)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status404NotFound,
                "Appointment was not found.");
        }

        var patientState = await GetPatientStateAsync(connection, request.IdPatient);
        var doctorState = await GetDoctorStateAsync(connection, request.IdDoctor);
        var stateError = GetStateError(patientState, doctorState);

        if (stateError is not null)
        {
            return stateError;
        }

        if (currentAppointment.Status == "Completed" &&
            IsDateChanged(currentAppointment.AppointmentDate, request.AppointmentDate))
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status409Conflict,
                "Completed appointment date cannot be changed.");
        }

        var hasConflict = await HasDoctorConflictAsync(
            connection,
            request.IdDoctor,
            request.AppointmentDate,
            idAppointment);

        if (normalizedStatus == "Scheduled" && hasConflict)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status409Conflict,
                "Doctor already has a scheduled appointment at this time.");
        }

        using var command = new SqlCommand("""
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient,
                IdDoctor = @IdDoctor,
                AppointmentDate = @AppointmentDate,
                Status = @Status,
                Reason = @Reason,
                InternalNotes = @InternalNotes
            WHERE IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = normalizedStatus!;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason.Trim();
        command.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value =
            string.IsNullOrWhiteSpace(request.InternalNotes) ? DBNull.Value : request.InternalNotes.Trim();

        await command.ExecuteNonQueryAsync();

        var updatedAppointment = await GetAppointmentDetailsAsync(connection, idAppointment);

        return ServiceResult<AppointmentDetailsDto>.Success(updatedAppointment!);
    }

    public async Task<ServiceResult<bool>> DeleteAppointmentAsync(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var currentAppointment = await GetAppointmentStateAsync(connection, idAppointment);

        if (currentAppointment is null)
        {
            return ServiceResult<bool>.Failure(
                StatusCodes.Status404NotFound,
                "Appointment was not found.");
        }

        if (currentAppointment.Status == "Completed")
        {
            return ServiceResult<bool>.Failure(
                StatusCodes.Status409Conflict,
                "Completed appointment cannot be deleted.");
        }

        using var command = new SqlCommand("""
            DELETE FROM dbo.Appointments
            WHERE IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await command.ExecuteNonQueryAsync();

        return ServiceResult<bool>.Success(true, StatusCodes.Status204NoContent);
    }

    private async Task<AppointmentDetailsDto?> GetAppointmentDetailsAsync(SqlConnection connection, int idAppointment)
    {
        using var command = new SqlCommand("""
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                a.InternalNotes,
                a.CreatedAt,
                p.IdPatient,
                p.FirstName AS PatientFirstName,
                p.LastName AS PatientLastName,
                p.Email AS PatientEmail,
                p.PhoneNumber AS PatientPhoneNumber,
                p.DateOfBirth AS PatientDateOfBirth,
                d.IdDoctor,
                d.FirstName AS DoctorFirstName,
                d.LastName AS DoctorLastName,
                d.LicenseNumber AS DoctorLicenseNumber,
                s.Name AS SpecializationName
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
            JOIN dbo.Specializations s ON s.IdSpecialization = d.IdSpecialization
            WHERE a.IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
            AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
            Status = reader.GetString(reader.GetOrdinal("Status")),
            Reason = reader.GetString(reader.GetOrdinal("Reason")),
            InternalNotes = reader.IsDBNull(reader.GetOrdinal("InternalNotes"))
                ? null
                : reader.GetString(reader.GetOrdinal("InternalNotes")),
            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
            IdPatient = reader.GetInt32(reader.GetOrdinal("IdPatient")),
            PatientFirstName = reader.GetString(reader.GetOrdinal("PatientFirstName")),
            PatientLastName = reader.GetString(reader.GetOrdinal("PatientLastName")),
            PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail")),
            PatientPhoneNumber = reader.GetString(reader.GetOrdinal("PatientPhoneNumber")),
            PatientDateOfBirth = reader.GetDateTime(reader.GetOrdinal("PatientDateOfBirth")),
            IdDoctor = reader.GetInt32(reader.GetOrdinal("IdDoctor")),
            DoctorFirstName = reader.GetString(reader.GetOrdinal("DoctorFirstName")),
            DoctorLastName = reader.GetString(reader.GetOrdinal("DoctorLastName")),
            DoctorLicenseNumber = reader.GetString(reader.GetOrdinal("DoctorLicenseNumber")),
            SpecializationName = reader.GetString(reader.GetOrdinal("SpecializationName"))
        };
    }

    private async Task<AppointmentState?> GetAppointmentStateAsync(SqlConnection connection, int idAppointment)
    {
        using var command = new SqlCommand("""
            SELECT Status, AppointmentDate
            FROM dbo.Appointments
            WHERE IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new AppointmentState
        {
            Status = reader.GetString(reader.GetOrdinal("Status")),
            AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate"))
        };
    }

    private async Task<ClinicEntityState> GetPatientStateAsync(SqlConnection connection, int idPatient)
    {
        using var command = new SqlCommand("""
            SELECT IsActive
            FROM dbo.Patients
            WHERE IdPatient = @IdPatient;
            """, connection);

        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = idPatient;

        var result = await command.ExecuteScalarAsync();

        if (result is null)
        {
            return ClinicEntityState.NotFound;
        }

        return Convert.ToBoolean(result) ? ClinicEntityState.Active : ClinicEntityState.Inactive;
    }

    private async Task<ClinicEntityState> GetDoctorStateAsync(SqlConnection connection, int idDoctor)
    {
        using var command = new SqlCommand("""
            SELECT IsActive
            FROM dbo.Doctors
            WHERE IdDoctor = @IdDoctor;
            """, connection);

        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;

        var result = await command.ExecuteScalarAsync();

        if (result is null)
        {
            return ClinicEntityState.NotFound;
        }

        return Convert.ToBoolean(result) ? ClinicEntityState.Active : ClinicEntityState.Inactive;
    }

    private async Task<bool> HasDoctorConflictAsync(
        SqlConnection connection,
        int idDoctor,
        DateTime appointmentDate,
        int? skippedAppointmentId)
    {
        using var command = new SqlCommand("""
            SELECT COUNT(1)
            FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND Status = N'Scheduled'
              AND (@SkippedAppointmentId IS NULL OR IdAppointment <> @SkippedAppointmentId);
            """, connection);

        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = appointmentDate;
        command.Parameters.Add("@SkippedAppointmentId", SqlDbType.Int).Value =
            (object?)skippedAppointmentId ?? DBNull.Value;

        var result = await command.ExecuteScalarAsync();

        return Convert.ToInt32(result) > 0;
    }

    private static ServiceResult<AppointmentDetailsDto>? GetStateError(
        ClinicEntityState patientState,
        ClinicEntityState doctorState)
    {
        if (patientState == ClinicEntityState.NotFound)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status404NotFound,
                "Patient was not found.");
        }

        if (doctorState == ClinicEntityState.NotFound)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status404NotFound,
                "Doctor was not found.");
        }

        if (patientState == ClinicEntityState.Inactive)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status409Conflict,
                "Patient is inactive.");
        }

        if (doctorState == ClinicEntityState.Inactive)
        {
            return ServiceResult<AppointmentDetailsDto>.Failure(
                StatusCodes.Status409Conflict,
                "Doctor is inactive.");
        }

        return null;
    }

    private static List<string> ValidateCreateRequest(CreateAppointmentRequestDto request)
    {
        var errors = new List<string>();

        if (request.IdPatient <= 0)
        {
            errors.Add("Patient id must be greater than zero.");
        }

        if (request.IdDoctor <= 0)
        {
            errors.Add("Doctor id must be greater than zero.");
        }

        if (request.AppointmentDate <= DateTime.Now)
        {
            errors.Add("Appointment date cannot be in the past.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors.Add("Reason cannot be empty.");
        }
        else if (request.Reason.Trim().Length > 250)
        {
            errors.Add("Reason cannot be longer than 250 characters.");
        }

        return errors;
    }

    private static List<string> ValidateUpdateRequest(UpdateAppointmentRequestDto request, string? normalizedStatus)
    {
        var errors = new List<string>();

        if (request.IdPatient <= 0)
        {
            errors.Add("Patient id must be greater than zero.");
        }

        if (request.IdDoctor <= 0)
        {
            errors.Add("Doctor id must be greater than zero.");
        }

        if (normalizedStatus is null)
        {
            errors.Add("Status must be one of: Scheduled, Completed, Cancelled.");
        }

        if (normalizedStatus == "Scheduled" && request.AppointmentDate <= DateTime.Now)
        {
            errors.Add("Scheduled appointment date cannot be in the past.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors.Add("Reason cannot be empty.");
        }
        else if (request.Reason.Trim().Length > 250)
        {
            errors.Add("Reason cannot be longer than 250 characters.");
        }

        if (!string.IsNullOrWhiteSpace(request.InternalNotes) && request.InternalNotes.Trim().Length > 500)
        {
            errors.Add("Internal notes cannot be longer than 500 characters.");
        }

        return errors;
    }

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return AllowedStatuses.FirstOrDefault(allowedStatus =>
            string.Equals(allowedStatus, status.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDateChanged(DateTime currentDate, DateTime newDate)
    {
        return Math.Abs((currentDate - newDate).TotalSeconds) >= 1;
    }
}