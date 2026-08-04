using System.Net;
using Oci.Common.Model;

namespace OracleHost.Services;

/// <summary>
/// Classifies OCI errors into retryable or fatal, matching the Python version's logic.
/// </summary>
public static class ErrorClassifier
{
    private static readonly HashSet<string> AbortCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LimitExceeded",
        "NotAuthorizedOrNotFound",
        "AuthFailure",
        "InvalidParameter",
        "InvalidParameterFormat",
        "BadRequest",
        "MissingConfig",
        "ConfigFileInvalid",
        "MethodNotAllowed"
    };

    private static readonly HashSet<int> RetryStatusCodes = new() { 429, 500, 503 };

    private static readonly HashSet<string> RetryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TooManyRequests",
        "InternalError"
    };

    public static ErrorClassification Classify(Exception ex, bool stopOnLimit = true)
    {
        string code = "", message = ex.Message ?? "";
        int status = 0;

        // The SDK raises the typed OciException for OCI service errors; other
        // exception types (network, timeout) carry no service code or status.
        if (ex is OciException ociException)
        {
            code = ociException.ServiceCode ?? "";
            status = (int)ociException.StatusCode;
        }

        // Hard failures - never retry
        if (string.Equals(code, "LimitExceeded", StringComparison.OrdinalIgnoreCase))
        {
            if (!stopOnLimit)
                return new ErrorClassification(ErrorKind.Retry, $"(stop_on_limit disabled) LimitExceeded: {message}");
            return new ErrorClassification(ErrorKind.Abort, $"LimitExceeded - free-tier quota reached: {message}");
        }

        if (AbortCodes.Contains(code))
            return new ErrorClassification(ErrorKind.Abort, $"{code}: {message}");

        // Capacity / transient issues - worth retrying
        if (string.Equals(code, "InternalError", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("capacity", StringComparison.OrdinalIgnoreCase))
            return new ErrorClassification(ErrorKind.Retry, "Out of host capacity");

        if (message.Contains("capacity", StringComparison.OrdinalIgnoreCase))
            return new ErrorClassification(ErrorKind.Retry, $"Capacity: {message}");

        if (RetryStatusCodes.Contains(status) || RetryCodes.Contains(code))
            return new ErrorClassification(ErrorKind.Retry, $"Transient error ({status}): {message}");

        // Network / connectivity errors
        if (ex is TimeoutException || ex is HttpRequestException ||
            message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return new ErrorClassification(ErrorKind.Retry, $"Network/connectivity error: {message}");

        if (!string.IsNullOrEmpty(code))
            return new ErrorClassification(ErrorKind.Abort, $"Unexpected service error ({code}): {message}");

        return new ErrorClassification(ErrorKind.Abort, $"Unexpected error: {ex}");
    }
}

public enum ErrorKind
{
    Retry,
    Abort
}

public record ErrorClassification(ErrorKind Kind, string Reason);
