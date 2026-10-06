using System;
using System.Collections.Concurrent;
using System.Net;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// Memory-bounded rate limiter for failed authentication attempts.
/// Enforces temporary backoff lockout per client IP address to prevent brute-force token guessing.
/// </summary>
public sealed class AuthenticationRateLimiter
{
    private sealed class FailureRecord
    {
        public int Count { get; set; }
        public DateTime FirstFailure { get; set; }
        public DateTime? LockoutUntil { get; set; }
    }

    private readonly ConcurrentDictionary<IPAddress, FailureRecord> _failures = new();
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _lockoutDuration;
    private readonly int _maxTrackedIps;

    public AuthenticationRateLimiter(
        int maxFailures = 5,
        TimeSpan? window = null,
        TimeSpan? lockoutDuration = null,
        int maxTrackedIps = 500)
    {
        _maxFailures = maxFailures;
        _window = window ?? TimeSpan.FromMinutes(1);
        _lockoutDuration = lockoutDuration ?? TimeSpan.FromMinutes(1);
        _maxTrackedIps = maxTrackedIps;
    }

    /// <summary>
    /// Checks whether the specified IP is currently locked out due to excessive failed attempts.
    /// </summary>
    public bool IsLockedOut(IPAddress ip, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        DateTime now = DateTime.UtcNow;

        if (_failures.TryGetValue(ip, out var record))
        {
            if (record.LockoutUntil.HasValue && record.LockoutUntil.Value > now)
            {
                retryAfter = record.LockoutUntil.Value - now;
                return true;
            }

            // Lockout expired, clean up
            if (record.LockoutUntil.HasValue && record.LockoutUntil.Value <= now)
            {
                _failures.TryRemove(ip, out _);
            }
        }

        return false;
    }

    /// <summary>
    /// Records a failed authentication attempt. Applies lockout if limit is reached.
    /// </summary>
    public void RecordFailedAttempt(IPAddress ip)
    {
        DateTime now = DateTime.UtcNow;

        // Prune if collection grows too large
        if (_failures.Count > _maxTrackedIps)
        {
            PruneStaleRecords(now);
        }

        _failures.AddOrUpdate(
            ip,
            _ => new FailureRecord
            {
                Count = 1,
                FirstFailure = now,
                LockoutUntil = null
            },
            (_, existing) =>
            {
                // Reset if outside rolling window
                if (now - existing.FirstFailure > _window && !existing.LockoutUntil.HasValue)
                {
                    existing.Count = 1;
                    existing.FirstFailure = now;
                    existing.LockoutUntil = null;
                }
                else
                {
                    existing.Count++;
                    if (existing.Count >= _maxFailures)
                    {
                        existing.LockoutUntil = now.Add(_lockoutDuration);
                    }
                }
                return existing;
            });
    }

    /// <summary>
    /// Clears any tracked failure records for an IP upon successful authentication.
    /// </summary>
    public void RecordSuccessfulAuth(IPAddress ip)
    {
        _failures.TryRemove(ip, out _);
    }

    /// <summary>
    /// Clears all rate limiting states.
    /// </summary>
    public void Reset()
    {
        _failures.Clear();
    }

    private void PruneStaleRecords(DateTime now)
    {
        foreach (var (key, value) in _failures)
        {
            if (value.LockoutUntil.HasValue && value.LockoutUntil.Value <= now)
            {
                _failures.TryRemove(key, out _);
            }
            else if (!value.LockoutUntil.HasValue && now - value.FirstFailure > _window)
            {
                _failures.TryRemove(key, out _);
            }
        }
    }
}
