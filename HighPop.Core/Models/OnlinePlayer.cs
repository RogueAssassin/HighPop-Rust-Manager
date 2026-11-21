namespace HighPop.Models;

public class OnlinePlayer
{
    public string Name             { get; set; } = string.Empty;
    public string SteamId          { get; set; } = string.Empty;   // Steam64 or UUID
    public int    Ping             { get; set; }
    public int    ConnectedSeconds { get; set; }
    public bool   IsSelected       { get; set; }
    public string StaffNotes       { get; set; } = string.Empty;
    public bool   IsWhitelisted    { get; set; }

    // Extra fields from Rust's WebRCON playerlist (empty or zero from the Steam query fallback).
    /// <summary>Client address as Rust reports it, without the port.</summary>
    public string Address          { get; set; } = string.Empty;
    /// <summary>Steam64 ID of the account owner; differs from <see cref="SteamId"/> for Steam Family Sharing.</summary>
    public string OwnerSteamId     { get; set; } = string.Empty;
    public double Health           { get; set; } = -1;
    /// <summary>Rust's anti-hack violation level (the JSON field is spelled "VoiationLevel").</summary>
    public double ViolationLevel   { get; set; }

    public string ConnectedText => ConnectedSeconds switch
    {
        < 60    => $"{ConnectedSeconds}s",
        < 3600  => $"{ConnectedSeconds / 60}m {ConnectedSeconds % 60}s",
        _       => $"{ConnectedSeconds / 3600}h {ConnectedSeconds % 3600 / 60}m",
    };

    public string PingText  => Ping > 0 ? $"{Ping} ms" : "—";
    public string SteamIdShort => SteamId.Length > 10
        ? "…" + SteamId[^8..]
        : SteamId;
    public string StaffNotesPreview => string.IsNullOrWhiteSpace(StaffNotes)
        ? "No staff notes"
        : StaffNotes;
    public string AccessText => IsWhitelisted ? "Whitelist allowed" : string.Empty;

    public string HealthText => Health < 0 ? "—" : Health <= 0 ? "Dead" : $"{Math.Round(Health):0} HP";
    public bool   IsFamilyShared => OwnerSteamId.Length > 0 && OwnerSteamId != "0" && OwnerSteamId != SteamId;
    public bool   HasViolations => ViolationLevel > 0;
    public string FlagsText
    {
        get
        {
            var flags = new List<string>(2);
            if (IsFamilyShared) flags.Add("Family share · owner " + OwnerSteamId);
            if (HasViolations) flags.Add($"Anti-hack violations {ViolationLevel:0.#}");
            return string.Join("  ·  ", flags);
        }
    }
    public string PingBand => Ping switch { <= 0 => "none", < 100 => "good", < 200 => "fair", _ => "poor" };
    public string SteamProfileUrl => SteamId.Length == 17 && SteamId.All(char.IsAsciiDigit)
        ? "https://steamcommunity.com/profiles/" + SteamId
        : string.Empty;
}
