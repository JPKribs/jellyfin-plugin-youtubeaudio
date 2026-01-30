using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Circuit breaker to prevent hammering a failing server.
/// Tracks consecutive failures and stops operations after threshold is reached.
/// </summary>
public class CircuitBreaker
{
    private readonly ILogger _logger;
    private readonly string _serviceName;
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;

    private int _consecutiveFailures;
    private DateTime? _circuitOpenedAt;
    private readonly object _lock = new();

    /// <summary>
    /// Gets whether the circuit is currently open (failing).
    /// </summary>
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                if (_circuitOpenedAt == null)
                {
                    return false;
                }

                // Check if cooldown period has elapsed
                if (DateTime.UtcNow - _circuitOpenedAt.Value >= _cooldownPeriod)
                {
                    // Reset circuit to half-open (allow one attempt)
                    _logger.LogInformation(
                        "Circuit breaker for {Service} cooldown elapsed, allowing retry attempt",
                        _serviceName);
                    return false;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Gets the number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>
    /// Gets the time remaining in cooldown, or null if circuit is closed.
    /// </summary>
    public TimeSpan? CooldownRemaining
    {
        get
        {
            lock (_lock)
            {
                if (_circuitOpenedAt == null)
                {
                    return null;
                }

                var elapsed = DateTime.UtcNow - _circuitOpenedAt.Value;
                if (elapsed >= _cooldownPeriod)
                {
                    return null;
                }

                return _cooldownPeriod - elapsed;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="logger">Logger for circuit breaker events.</param>
    /// <param name="serviceName">Name of the service being protected.</param>
    /// <param name="failureThreshold">Number of consecutive failures before opening circuit.</param>
    /// <param name="cooldownPeriod">Time to wait before allowing retry after circuit opens.</param>
    public CircuitBreaker(
        ILogger logger,
        string serviceName,
        int failureThreshold = 5,
        TimeSpan? cooldownPeriod = null)
    {
        _logger = logger;
        _serviceName = serviceName;
        _failureThreshold = failureThreshold;
        _cooldownPeriod = cooldownPeriod ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Records a successful operation, resetting the failure count.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0 || _circuitOpenedAt != null)
            {
                _logger.LogInformation(
                    "Circuit breaker for {Service} reset after successful operation",
                    _serviceName);
            }

            _consecutiveFailures = 0;
            _circuitOpenedAt = null;
        }
    }

    /// <summary>
    /// Records a failed operation. Opens circuit if threshold is reached.
    /// </summary>
    /// <param name="errorMessage">Optional error message for logging.</param>
    public void RecordFailure(string? errorMessage = null)
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures >= _failureThreshold && _circuitOpenedAt == null)
            {
                _circuitOpenedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "Circuit breaker OPEN for {Service}: {Failures} consecutive failures. " +
                    "Sync operations will pause for {Cooldown} minutes. Last error: {Error}",
                    _serviceName,
                    _consecutiveFailures,
                    _cooldownPeriod.TotalMinutes,
                    errorMessage ?? "Unknown");
            }
            else if (_circuitOpenedAt == null)
            {
                _logger.LogDebug(
                    "Circuit breaker for {Service}: failure {Current}/{Threshold}. Error: {Error}",
                    _serviceName,
                    _consecutiveFailures,
                    _failureThreshold,
                    errorMessage ?? "Unknown");
            }
        }
    }

    /// <summary>
    /// Checks if the circuit allows an operation. Returns false if circuit is open.
    /// </summary>
    /// <param name="reason">Output parameter with reason if operation is blocked.</param>
    /// <returns>True if operation should proceed, false if circuit is open.</returns>
    public bool AllowOperation(out string? reason)
    {
        lock (_lock)
        {
            if (!IsOpen)
            {
                reason = null;
                return true;
            }

            var remaining = CooldownRemaining;
            reason = $"Circuit breaker open for {_serviceName}: " +
                     $"{_consecutiveFailures} consecutive failures. " +
                     $"Retry in {remaining?.TotalSeconds:F0} seconds.";
            return false;
        }
    }

    /// <summary>
    /// Resets the circuit breaker to closed state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _circuitOpenedAt = null;
            _logger.LogInformation("Circuit breaker for {Service} manually reset", _serviceName);
        }
    }
}
