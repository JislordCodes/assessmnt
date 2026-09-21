namespace ExpenseClassifier.Services;

/// <summary>
/// Raised when an expense could not be classified because the upstream model failed
/// (rate limit, timeout, transport error, or two consecutive malformed responses).
/// Carries the HTTP status the API should surface, so the endpoint layer stays free of provider details.
/// </summary>
public sealed class ClassificationFailedException(
    string message,
    int statusCode,
    Exception? innerException = null,
    int? retryAfterSeconds = null) : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
