namespace HighPop.Models;

public class ServerTemplate
{
    public string Id          { get; set; } = Guid.NewGuid().ToString();
    public string Name        { get; set; } = string.Empty;
    public string GameId      { get; set; } = string.Empty;
    public string GameName    { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Rust preset category and tags
    public string       Category { get; set; } = string.Empty;
    public List<string> Tags     { get; set; } = [];

    // Rust server settings
    public int    DefaultPort      { get; set; }
    public int    DefaultQueryPort { get; set; }
    public int    MaxPlayers       { get; set; }
    public bool   AutoRestart      { get; set; }
    public bool   KeepOnline       { get; set; } = true;
    public bool   AutoUpdate       { get; set; }
    public bool   BackupEnabled    { get; set; }
    public int    BackupRetention  { get; set; } = 5;
    public string CustomArgs       { get; set; } = string.Empty;
    public string ProcessPriority  { get; set; } = "Normal";
    public Dictionary<string, string> GameSpecificSettings { get; set; } = new();
}
