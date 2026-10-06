using System;

namespace AxePower.Core.Models.ShareScreen;

/// <summary>
/// Represents an active connected participant in a Share Screen session (viewer, controller, or voice participant).
/// Provides clear host-side indication of all connected endpoints and their roles.
/// </summary>
public sealed record ShareParticipant(
    string Id,
    string Endpoint,
    string Role,
    DateTime ConnectedAt);
