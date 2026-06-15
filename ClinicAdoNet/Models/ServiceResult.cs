using Microsoft.AspNetCore.Http;

namespace ClinicAdoNet.Models;

public class ServiceResult<T>
{
    public bool IsSuccess { get; set; }
    public int StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
    public T? Data { get; set; }

    public static ServiceResult<T> Success(T data, int statusCode = StatusCodes.Status200OK)
    {
        return new ServiceResult<T>
        {
            IsSuccess = true,
            StatusCode = statusCode,
            Data = data
        };
    }

    public static ServiceResult<T> Failure(int statusCode, string message, List<string>? errors = null)
    {
        return new ServiceResult<T>
        {
            IsSuccess = false,
            StatusCode = statusCode,
            Message = message,
            Errors = errors ?? new List<string>()
        };
    }
}