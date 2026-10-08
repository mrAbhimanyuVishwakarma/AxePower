using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AxePower.Core.Services.LanDrop;

/// <summary>
/// The devices that have used this session, keyed by address. Removing one cancels whatever it
/// still has in flight (a download, an event stream).
/// </summary>
internal sealed class LanDropClientRegistry
{
    private readonly ConcurrentDictionary<string, LanDropClientSession> _clients = new();
    private readonly TimeSpan _idleTimeout;

    public LanDropClientRegistry(TimeSpan idleTimeout) => _idleTimeout = idleTimeout;

    /// <summary>Records activity from <paramref name="address"/>; <paramref name="added"/> is true for a new device.</summary>
    public LanDropClientSession Track(string address, string userAgent, out bool added)
    {
        string deviceName = LanDropDeviceNames.ForSession(userAgent);
        bool isNew = false;
        var session = _clients.AddOrUpdate(
            address,
            _ =>
            {
                isNew = true;
                return new LanDropClientSession
                {
                    ClientIp = address,
                    DeviceName = deviceName,
                    UserAgent = userAgent,
                    ConnectedAt = DateTime.Now,
                    LastActivity = DateTime.Now
                };
            },
            (_, existing) =>
            {
                existing.LastActivity = DateTime.Now;
                if (!string.IsNullOrEmpty(userAgent))
                {
                    existing.UserAgent = userAgent;
                    existing.DeviceName = deviceName;
                }
                return existing;
            });
        added = isNew;
        return session;
    }

    /// <summary>Active devices, most recent first, after dropping idle ones.</summary>
    public IReadOnlyList<LanDropClientSession> Snapshot()
    {
        PruneIdle();
        return _clients.Values.OrderByDescending(c => c.LastActivity).ToList();
    }

    /// <summary>Drops clients idle past the limit and ends anything they still have in flight.</summary>
    public bool PruneIdle()
    {
        DateTime cutoff = DateTime.Now - _idleTimeout;
        bool removed = false;
        foreach (var (key, client) in _clients)
        {
            if (client.LastActivity < cutoff && _clients.TryRemove(key, out _))
            {
                End(client);
                removed = true;
            }
        }
        return removed;
    }

    /// <summary>Removes one device by its session id or its address, ending its requests.</summary>
    public LanDropClientSession? Remove(string idOrAddress)
    {
        // Clients are keyed by address, but the UI identifies them by their session id; accept either.
        var key = _clients.ContainsKey(idOrAddress)
            ? idOrAddress
            : _clients.FirstOrDefault(pair => pair.Value.Id == idOrAddress).Key;
        if (key == null || !_clients.TryRemove(key, out var client)) return null;
        End(client);
        return client;
    }

    /// <summary>Ends every device's requests and forgets them all.</summary>
    public void EndAll()
    {
        foreach (var client in _clients.Values) End(client);
        _clients.Clear();
    }

    private static void End(LanDropClientSession client)
    {
        try { client.SessionCts.Cancel(); }
        catch (ObjectDisposedException) { /* The session already ended. */ }
        catch (AggregateException)
        {
            // A request's cancellation callback threw. The token is cancelled regardless, so the
            // device is still cut off; carrying on matters more, so the remaining devices end too.
        }
    }
}
