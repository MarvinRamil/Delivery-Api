using System;
using System.Collections.Generic;

namespace BeeLogistics.Shared.Abstractions;

/// <summary>
/// Why a <see cref="Result"/> failed, so the presentation layer can pick an HTTP status
/// without inspecting the error message. <see cref="Failure"/> is the default for
/// <c>Fail</c>, which keeps every pre-existing call site mapping to 400 as before.
/// </summary>
public enum ResultErrorKind
{
    None = 0,
    Failure,
    NotFound,
    Forbidden
}

public class Result
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    public List<string> Errors { get; } = new();
    public bool IsFailure => !IsSuccess;
    public ResultErrorKind ErrorKind { get; }

    protected Result(bool isSuccess, string? error = null, List<string>? errors = null, ResultErrorKind errorKind = ResultErrorKind.None)
    {
        IsSuccess = isSuccess;
        Error = error;
        if (errors != null) Errors = errors;
        ErrorKind = isSuccess ? ResultErrorKind.None : errorKind;
    }

    public static Result Fail(string message) => new(false, message, errorKind: ResultErrorKind.Failure);
    public static Result Fail(List<string> errors) => new(false, errors: errors, errorKind: ResultErrorKind.Failure);
    public static Result<T> Fail<T>(string message) => new(default, false, message, errorKind: ResultErrorKind.Failure);
    public static Result<T> Fail<T>(List<string> errors) => new(default, false, errors: errors, errorKind: ResultErrorKind.Failure);
    public static Result Ok() => new(true);
    public static Result<T> Ok<T>(T value) => new(value, true);
    public static Result NotFound(string message = "Resource not found") => new(false, message, errorKind: ResultErrorKind.NotFound);
    public static Result<T> NotFound<T>(string message = "Resource not found") => new(default, false, message, ResultErrorKind.NotFound);

    /// <summary>
    /// The caller is authenticated but not allowed to act on this resource. Maps to 403,
    /// unlike <see cref="Fail(string)"/>, which maps to 400.
    /// </summary>
    public static Result Forbidden(string message = "You do not have access to this resource") => new(false, message, errorKind: ResultErrorKind.Forbidden);

    /// <inheritdoc cref="Forbidden(string)"/>
    public static Result<T> Forbidden<T>(string message = "You do not have access to this resource") => new(default, false, message, ResultErrorKind.Forbidden);
}

public class Result<T> : Result
{
    public T? Value { get; }

    protected internal Result(T? value, bool isSuccess, string? error = null, ResultErrorKind errorKind = ResultErrorKind.None, List<string>? errors = null)
        : base(isSuccess, error, errors, errorKind)
    {
        Value = value;
    }
}
