using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Provides retry logic with exponential backoff for network operations.
/// </summary>
public static class RetryPolicy
{
    /// <summary>
    /// Default base delay for exponential backoff (1 second).
    /// </summary>
    private const int BaseDelayMs = 1000;

    /// <summary>
    /// Maximum delay cap to prevent excessive wait times (30 seconds).
    /// </summary>
    private const int MaxDelayMs = 30000;

    /// <summary>
    /// Executes an async operation with retry logic and exponential backoff.
    /// </summary>
    /// <typeparam name="T">Return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="logger">Logger for retry information.</param>
    /// <param name="operationName">Name of the operation for logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the operation.</returns>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxRetries,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        Exception? lastException = null;

        while (attempt <= maxRetries)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientException(ex))
            {
                lastException = ex;
                attempt++;

                if (attempt > maxRetries)
                {
                    logger.LogError(ex, "{Operation} failed after {Attempts} attempts", operationName, attempt);
                    throw;
                }

                var delay = CalculateDelay(attempt);
                logger.LogWarning(
                    "{Operation} failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms: {Error}",
                    operationName, attempt, maxRetries + 1, delay, ex.Message);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
            catch (Exception)
            {
                // Non-transient exception, don't retry
                throw;
            }
        }

        // Should not reach here, but just in case
        throw lastException ?? new InvalidOperationException("Retry failed without exception");
    }

    /// <summary>
    /// Executes an async operation with retry logic and exponential backoff (void return).
    /// </summary>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="logger">Logger for retry information.</param>
    /// <param name="operationName">Name of the operation for logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task ExecuteWithRetryAsync(
        Func<CancellationToken, Task> operation,
        int maxRetries,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            },
            maxRetries,
            logger,
            operationName,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Calculates the delay for exponential backoff with jitter.
    /// </summary>
    /// <param name="attempt">Current attempt number (1-based).</param>
    /// <returns>Delay in milliseconds.</returns>
    private static int CalculateDelay(int attempt)
    {
        // Exponential backoff: 1s, 2s, 4s, 8s, ... capped at MaxDelayMs
        var clampedExponent = Math.Min(attempt - 1, 20); // Cap exponent to prevent int overflow
        var exponentialDelay = (int)(BaseDelayMs * Math.Pow(2, clampedExponent));
        var cappedDelay = Math.Min(exponentialDelay, MaxDelayMs);

        // Add jitter (±25%) to prevent thundering herd
        var jitter = Random.Shared.Next(-(cappedDelay / 4), cappedDelay / 4);
        return cappedDelay + jitter;
    }

    /// <summary>
    /// Determines if an exception is transient and should trigger a retry.
    /// </summary>
    /// <param name="ex">Exception to check.</param>
    /// <returns>True if the exception is transient.</returns>
    private static bool IsTransientException(Exception ex)
    {
        // HttpRequestException with transient status codes
        if (ex is HttpRequestException httpEx)
        {
            // Check for transient HTTP status codes
            if (httpEx.StatusCode.HasValue)
            {
                return IsTransientStatusCode(httpEx.StatusCode.Value);
            }

            // Network-level failures are generally transient
            return true;
        }

        // TaskCanceledException due to timeout (not user cancellation)
        if (ex is TaskCanceledException taskCanceledEx && taskCanceledEx.InnerException is TimeoutException)
        {
            return true;
        }

        // IO exceptions are often transient
        if (ex is System.IO.IOException)
        {
            return true;
        }

        // Socket exceptions are transient
        if (ex is System.Net.Sockets.SocketException)
        {
            return true;
        }

        // Check inner exception
        if (ex.InnerException != null)
        {
            return IsTransientException(ex.InnerException);
        }

        return false;
    }

    /// <summary>
    /// Determines if an HTTP status code indicates a transient error.
    /// </summary>
    /// <param name="statusCode">HTTP status code to check.</param>
    /// <returns>True if the status code indicates a transient error.</returns>
    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout => true,         // 408
            HttpStatusCode.TooManyRequests => true,        // 429
            HttpStatusCode.InternalServerError => true,    // 500
            HttpStatusCode.BadGateway => true,             // 502
            HttpStatusCode.ServiceUnavailable => true,     // 503
            HttpStatusCode.GatewayTimeout => true,         // 504
            _ => false
        };
    }
}
