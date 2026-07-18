namespace HighPop.Games;

/// <summary>
/// Rust-only server profile registry. HighPop intentionally ships and loads no other game type.
/// </summary>
public static class GameRegistry
{
    private static readonly IGamePlugin Rust = new RustPlugin();

    public static IGamePlugin RustServer => Rust;

    public static IEnumerable<IGamePlugin> All => [Rust];

    public static IGamePlugin? Get(string gameId)
        => gameId.Equals(Rust.GameId, StringComparison.OrdinalIgnoreCase) ? Rust : null;
}
