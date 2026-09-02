using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Core.Models;

namespace Launcher.Core.Abstractions;

/// <summary>
/// Discovers the games credited to an itch.io profile.
///
/// Reads the public profile page rather than the API on purpose: /profile/games returns only
/// games the account *owns*, which silently drops collaborations hosted under other accounts.
/// The profile page lists everything the user is credited on.
/// </summary>
public interface IItchCatalogService
{
    Task<CatalogResult> FetchProfileGamesAsync(string profileUsername, CancellationToken ct = default);
}

/// <summary>
/// Catalog outcome. Carries a diagnostic so an empty library can explain itself in the UI
/// instead of just rendering nothing.
/// </summary>
public record CatalogResult(
    IReadOnlyList<GameInfo> Games,
    bool FromCache,
    string? Diagnostic = null
)
{
    public bool IsEmpty => Games.Count == 0;

    public static CatalogResult Failed(string diagnostic) =>
        new(new List<GameInfo>(), false, diagnostic);
}
