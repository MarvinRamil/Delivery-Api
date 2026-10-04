namespace BeeLogistics.Shared.DTOs;

public class ApiResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public List<string> Errors { get; set; } = new();

    public static ApiResponse Ok(string? message = null) => new() { Success = true, Message = message };
    public static ApiResponse Fail(string message) => new() { Success = false, Message = message };
    public static ApiResponse Fail(List<string> errors) => new() { Success = false, Errors = errors };
}

public class ApiResponse<T> : ApiResponse
{
    public T? Data { get; set; }

    public static ApiResponse<T> Ok(T data, string? message = null) => new() { Success = true, Data = data, Message = message };
    public new static ApiResponse<T> Fail(string message) => new() { Success = false, Message = message };
    public new static ApiResponse<T> Fail(List<string> errors) => new() { Success = false, Errors = errors };
}
