using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HighPop.Services;

/// <summary>
/// Validated Rust console commands for the Players tab tools. Every command targets a player by
/// 17-digit Steam64 ID where possible: names can contain spaces, quotes and look-alike characters,
/// and Rust's player lookup matches partial names.
/// </summary>
public static class RustPlayerCommands
{
    public const int MaxGiveAmount = 1_000_000;
    private static readonly Regex ShortNamePattern = new("^[a-z0-9][a-z0-9_.\\-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex LoadoutPattern = new("^[A-Za-z0-9_\\-]{1,64}$", RegexOptions.Compiled);

    public static bool IsItemShortName(string? value) =>
        value != null && ShortNamePattern.IsMatch(value.Trim());

    /// <summary><c>inventory.giveto &lt;player&gt; &lt;item&gt; &lt;amount&gt; [skin]</c>.</summary>
    public static bool TryGive(string target, string shortName, int amount, ulong skin, out string command, out string error)
    {
        command = string.Empty;
        if (!TryTarget(target, out var who, out error)) return false;
        if (!TryItem(shortName, amount, out var item, out error)) return false;
        command = $"inventory.giveto {who} \"{item}\" {amount}" + (skin > 0 ? $" {skin}" : string.Empty);
        return true;
    }

    /// <summary><c>inventory.giveall &lt;item&gt; &lt;amount&gt;</c> gives every connected player the item.</summary>
    public static bool TryGiveAll(string shortName, int amount, out string command, out string error)
    {
        command = string.Empty;
        if (!TryItem(shortName, amount, out var item, out error)) return false;
        command = $"inventory.giveall \"{item}\" {amount}";
        return true;
    }

    public static bool TryTeleport(string player, string destinationPlayer, out string command, out string error)
    {
        command = string.Empty;
        if (!TryTarget(player, out var who, out error)) return false;
        if (!TryTarget(destinationPlayer, out var to, out error)) return false;
        if (who == to)
        {
            error = "Choose two different players to teleport.";
            return false;
        }
        command = $"teleport {who} {to}";
        return true;
    }

    public static bool TryDeployLoadout(string loadout, string target, out string command, out string error)
    {
        command = string.Empty;
        loadout = (loadout ?? string.Empty).Trim();
        if (!LoadoutPattern.IsMatch(loadout))
        {
            error = "Loadout names are letters, numbers, - and _ (see List loadouts).";
            return false;
        }
        if (!TryTarget(target, out var who, out error)) return false;
        command = $"inventory.deployloadout {loadout} {who}";
        return true;
    }

    /// <summary>Single-player actions that take only the target.</summary>
    public static bool TryPlayerAction(RustPlayerAction action, string target, out string command, out string error)
    {
        command = string.Empty;
        if (!TryTarget(target, out var who, out error)) return false;
        command = action switch
        {
            RustPlayerAction.Mute => $"mute {who}",
            RustPlayerAction.Unmute => $"unmute {who}",
            RustPlayerAction.Kill => $"killplayer {who}",
            RustPlayerAction.ClearInventory => $"inventory.clearinventory {who}",
            RustPlayerAction.UnlockBlueprints => $"inventory.unlockall {who}",
            RustPlayerAction.ResetBlueprints => $"inventory.resetbp {who}",
            RustPlayerAction.TeamInfo => $"teaminfo {who}",
            _ => string.Empty,
        };
        return command.Length > 0;
    }

    /// <summary>Moderator changes, saved with <c>server.writecfg</c> so they survive a restart.</summary>
    public static bool TrySetModerator(string steamId, string name, bool grant, out string[] commands, out string error)
    {
        commands = [];
        error = string.Empty;
        steamId = (steamId ?? string.Empty).Trim();
        if (!RustModerationCommands.IsSteamId(steamId))
        {
            error = "Moderator changes need a 17-digit Steam64 ID.";
            return false;
        }
        var cleanName = RustModerationCommands.CleanQuoted(name);
        commands = grant
            ? [$"moderatorid {steamId} \"{(cleanName.Length > 0 ? cleanName : steamId)}\" \"Added from HighPop\"", "server.writecfg"]
            : [$"removemoderator {steamId}", "server.writecfg"];
        return true;
    }

    public static bool TrySkipQueue(string steamId, out string command, out string error)
    {
        command = string.Empty;
        error = string.Empty;
        steamId = (steamId ?? string.Empty).Trim();
        if (!RustModerationCommands.IsSteamId(steamId))
        {
            error = "Queue skip needs a 17-digit Steam64 ID.";
            return false;
        }
        command = $"skipqueueid {steamId}";
        return true;
    }

    private static bool TryTarget(string target, out string quoted, out string error)
    {
        quoted = string.Empty;
        error = string.Empty;
        var value = RustModerationCommands.CleanQuoted(target);
        if (value.Length == 0)
        {
            error = "Select a player first (or type a name or Steam64 ID).";
            return false;
        }
        quoted = RustModerationCommands.IsSteamId(value) ? value : $"\"{value}\"";
        return true;
    }

    private static bool TryItem(string shortName, int amount, out string item, out string error)
    {
        item = (shortName ?? string.Empty).Trim().ToLowerInvariant();
        error = string.Empty;
        if (!IsItemShortName(item))
        {
            error = "Pick an item, or type a Rust item short name such as rifle.ak or metal.fragments.";
            return false;
        }
        if (amount is < 1 or > MaxGiveAmount)
        {
            error = $"Amount must be between 1 and {MaxGiveAmount:N0}.";
            return false;
        }
        return true;
    }
}

public enum RustPlayerAction
{
    Mute,
    Unmute,
    Kill,
    ClearInventory,
    UnlockBlueprints,
    ResetBlueprints,
    TeamInfo,
}

/// <summary>A Rust item for the give menu.</summary>
public sealed record RustCatalogItem(string ShortName, string DisplayName, string Category, int DefaultAmount)
{
    public string Label => $"{DisplayName}  ·  {ShortName}";
}

/// <summary>
/// Common Rust items by category. Short names are what <c>inventory.giveto</c> expects; any other
/// item can still be typed by short name in the give menu.
/// </summary>
public static class RustItemCatalog
{
    public const string AllCategories = "All";

    public static IReadOnlyList<RustCatalogItem> Items { get; } =
    [
        // Resources
        new("wood", "Wood", "Resources", 1000),
        new("stones", "Stones", "Resources", 1000),
        new("metal.fragments", "Metal Fragments", "Resources", 1000),
        new("metal.refined", "High Quality Metal", "Resources", 100),
        new("metal.ore", "Metal Ore", "Resources", 1000),
        new("hq.metal.ore", "High Quality Metal Ore", "Resources", 100),
        new("sulfur.ore", "Sulfur Ore", "Resources", 1000),
        new("sulfur", "Sulfur", "Resources", 1000),
        new("charcoal", "Charcoal", "Resources", 1000),
        new("gunpowder", "Gun Powder", "Resources", 1000),
        new("cloth", "Cloth", "Resources", 500),
        new("leather", "Leather", "Resources", 500),
        new("lowgradefuel", "Low Grade Fuel", "Resources", 500),
        new("crude.oil", "Crude Oil", "Resources", 500),
        new("scrap", "Scrap", "Resources", 500),
        new("fat.animal", "Animal Fat", "Resources", 500),
        new("bone.fragments", "Bone Fragments", "Resources", 500),

        // Components
        new("techparts", "Tech Trash", "Components", 10),
        new("gears", "Gears", "Components", 10),
        new("metalpipe", "Metal Pipe", "Components", 10),
        new("metalspring", "Metal Spring", "Components", 10),
        new("sheetmetal", "Sheet Metal", "Components", 10),
        new("roadsigns", "Road Signs", "Components", 10),
        new("rope", "Rope", "Components", 10),
        new("tarp", "Tarp", "Components", 10),
        new("sewingkit", "Sewing Kit", "Components", 10),
        new("riflebody", "Rifle Body", "Components", 5),
        new("smgbody", "SMG Body", "Components", 5),
        new("semibody", "Semi Automatic Body", "Components", 5),
        new("propanetank", "Empty Propane Tank", "Components", 5),

        // Weapons
        new("rifle.ak", "Assault Rifle", "Weapons", 1),
        new("rifle.lr300", "LR-300 Assault Rifle", "Weapons", 1),
        new("rifle.bolt", "Bolt Action Rifle", "Weapons", 1),
        new("rifle.l96", "L96 Rifle", "Weapons", 1),
        new("rifle.m39", "M39 Rifle", "Weapons", 1),
        new("rifle.semiauto", "Semi-Automatic Rifle", "Weapons", 1),
        new("rifle.sks", "SKS", "Weapons", 1),
        new("lmg.m249", "M249", "Weapons", 1),
        new("smg.mp5", "MP5A4", "Weapons", 1),
        new("smg.thompson", "Thompson", "Weapons", 1),
        new("smg.2", "Custom SMG", "Weapons", 1),
        new("pistol.python", "Python Revolver", "Weapons", 1),
        new("pistol.semiauto", "Semi-Automatic Pistol", "Weapons", 1),
        new("pistol.revolver", "Revolver", "Weapons", 1),
        new("pistol.m92", "M92 Pistol", "Weapons", 1),
        new("pistol.eoka", "Eoka Pistol", "Weapons", 1),
        new("pistol.nailgun", "Nailgun", "Weapons", 1),
        new("shotgun.pump", "Pump Shotgun", "Weapons", 1),
        new("shotgun.spas12", "Spas-12 Shotgun", "Weapons", 1),
        new("shotgun.double", "Double Barrel Shotgun", "Weapons", 1),
        new("shotgun.waterpipe", "Waterpipe Shotgun", "Weapons", 1),
        new("bow.hunting", "Hunting Bow", "Weapons", 1),
        new("crossbow", "Crossbow", "Weapons", 1),
        new("rocket.launcher", "Rocket Launcher", "Weapons", 1),
        new("knife.combat", "Combat Knife", "Weapons", 1),
        new("salvaged.sword", "Salvaged Sword", "Weapons", 1),

        // Ammunition
        new("ammo.rifle", "5.56 Rifle Ammo", "Ammunition", 128),
        new("ammo.rifle.hv", "HV 5.56 Rifle Ammo", "Ammunition", 128),
        new("ammo.rifle.explosive", "Explosive 5.56 Rifle Ammo", "Ammunition", 64),
        new("ammo.rifle.incendiary", "Incendiary 5.56 Rifle Ammo", "Ammunition", 64),
        new("ammo.pistol", "Pistol Bullet", "Ammunition", 128),
        new("ammo.pistol.hv", "HV Pistol Ammo", "Ammunition", 128),
        new("ammo.shotgun", "12 Gauge Buckshot", "Ammunition", 64),
        new("ammo.shotgun.slug", "12 Gauge Slug", "Ammunition", 64),
        new("ammo.rocket.basic", "Rocket", "Ammunition", 6),
        new("ammo.rocket.hv", "High Velocity Rocket", "Ammunition", 6),
        new("arrow.wooden", "Wooden Arrow", "Ammunition", 64),
        new("arrow.hv", "High Velocity Arrow", "Ammunition", 64),
        new("ammo.nailgun.nails", "Nailgun Nails", "Ammunition", 64),

        // Explosives
        new("explosive.timed", "Timed Explosive Charge (C4)", "Explosives", 4),
        new("explosive.satchel", "Satchel Charge", "Explosives", 4),
        new("grenade.f1", "F1 Grenade", "Explosives", 5),
        new("grenade.beancan", "Beancan Grenade", "Explosives", 5),
        new("supply.signal", "Supply Signal", "Explosives", 1),

        // Armor and clothing
        new("metal.facemask", "Metal Facemask", "Armor", 1),
        new("metal.plate.torso", "Metal Chest Plate", "Armor", 1),
        new("heavy.plate.helmet", "Heavy Plate Helmet", "Armor", 1),
        new("heavy.plate.jacket", "Heavy Plate Jacket", "Armor", 1),
        new("heavy.plate.pants", "Heavy Plate Pants", "Armor", 1),
        new("roadsign.kilt", "Road Sign Kilt", "Armor", 1),
        new("roadsign.jacket", "Road Sign Jacket", "Armor", 1),
        new("roadsign.gloves", "Roadsign Gloves", "Armor", 1),
        new("coffeecan.helmet", "Coffee Can Helmet", "Armor", 1),
        new("riot.helmet", "Riot Helmet", "Armor", 1),
        new("hoodie", "Hoodie", "Armor", 1),
        new("pants", "Pants", "Armor", 1),
        new("shoes.boots", "Boots", "Armor", 1),
        new("tactical.gloves", "Tactical Gloves", "Armor", 1),
        new("hazmatsuit", "Hazmat Suit", "Armor", 1),

        // Medical and food
        new("syringe.medical", "Medical Syringe", "Medical", 10),
        new("largemedkit", "Large Medkit", "Medical", 5),
        new("bandage", "Bandage", "Medical", 10),
        new("antiradpills", "Anti-Radiation Pills", "Medical", 10),

        // Tools
        new("hammer", "Hammer", "Tools", 1),
        new("building.planner", "Building Plan", "Tools", 1),
        new("hatchet", "Hatchet", "Tools", 1),
        new("pickaxe", "Pickaxe", "Tools", 1),
        new("axe.salvaged", "Salvaged Axe", "Tools", 1),
        new("icepick.salvaged", "Salvaged Icepick", "Tools", 1),
        new("jackhammer", "Jackhammer", "Tools", 1),
        new("chainsaw", "Chainsaw", "Tools", 1),
        new("torch", "Torch", "Tools", 1),
        new("flashlight.held", "Flashlight", "Tools", 1),
        new("binoculars", "Binoculars", "Tools", 1),
        new("wiretool", "Wire Tool", "Tools", 1),
        new("keycard_green", "Green Keycard", "Tools", 1),
        new("keycard_blue", "Blue Keycard", "Tools", 1),
        new("keycard_red", "Red Keycard", "Tools", 1),

        // Building and deployables
        new("cupboard.tool", "Tool Cupboard", "Deployables", 1),
        new("lock.code", "Code Lock", "Deployables", 5),
        new("door.hinged.metal", "Sheet Metal Door", "Deployables", 2),
        new("door.double.hinged.metal", "Sheet Metal Double Door", "Deployables", 2),
        new("door.hinged.toptier", "Armored Door", "Deployables", 2),
        new("door.double.hinged.toptier", "Armored Double Door", "Deployables", 2),
        new("wall.frame.garagedoor", "Garage Door", "Deployables", 2),
        new("wall.external.high.stone", "High External Stone Wall", "Deployables", 10),
        new("gates.external.high", "High External Wooden Gate", "Deployables", 1),
        new("box.wooden.large", "Large Wood Box", "Deployables", 2),
        new("furnace", "Furnace", "Deployables", 2),
        new("furnace.large", "Large Furnace", "Deployables", 1),
        new("small.oil.refinery", "Small Oil Refinery", "Deployables", 1),
        new("workbench1", "Workbench Level 1", "Deployables", 1),
        new("workbench2", "Workbench Level 2", "Deployables", 1),
        new("workbench3", "Workbench Level 3", "Deployables", 1),
        new("research.table", "Research Table", "Deployables", 1),
        new("sleepingbag", "Sleeping Bag", "Deployables", 2),
        new("bed", "Bed", "Deployables", 1),
        new("autoturret", "Auto Turret", "Deployables", 1),
        new("vending.machine", "Vending Machine", "Deployables", 1),
    ];

    public static IReadOnlyList<string> Categories { get; } =
        [AllCategories, .. Items.Select(i => i.Category).Distinct()];

    public static IReadOnlyList<RustCatalogItem> Filter(string? category, string? search)
    {
        var query = (search ?? string.Empty).Trim();
        return Items
            .Where(i => string.IsNullOrEmpty(category) || category == AllCategories || i.Category == category)
            .Where(i => query.Length == 0
                || i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || i.ShortName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static RustCatalogItem? Find(string? shortName) =>
        Items.FirstOrDefault(i => i.ShortName.Equals((shortName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Live server numbers from Rust's <c>serverinfo</c> (a JSON object).</summary>
public sealed record RustServerInfo(
    int Players,
    int MaxPlayers,
    int Queued,
    int Joining,
    int EntityCount,
    int Framerate,
    int MemoryMb,
    long UptimeSeconds,
    string GameTime,
    string Map)
{
    public string PlayersText => Queued > 0 || Joining > 0
        ? $"{Players}/{MaxPlayers} (+{Queued} queued, {Joining} joining)"
        : $"{Players}/{MaxPlayers}";

    public string UptimeText => UptimeSeconds switch
    {
        < 3600 => $"{UptimeSeconds / 60}m",
        < 86400 => $"{UptimeSeconds / 3600}h {UptimeSeconds % 3600 / 60}m",
        _ => $"{UptimeSeconds / 86400}d {UptimeSeconds % 86400 / 3600}h",
    };

    public string GameTimeText
    {
        get
        {
            // Rust reports e.g. "11/09/2026 14:32:05"; only the in-game clock matters.
            var space = GameTime.LastIndexOf(' ');
            var clock = space >= 0 ? GameTime[(space + 1)..] : GameTime;
            return clock.Length >= 5 ? clock[..5] : clock;
        }
    }

    public static bool TryParse(string json, out RustServerInfo info)
    {
        info = new RustServerInfo(0, 0, 0, 0, 0, 0, 0, 0, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("MaxPlayers", out _) && !root.TryGetProperty("Players", out _)) return false;
            info = new RustServerInfo(
                Int(root, "Players"),
                Int(root, "MaxPlayers"),
                Int(root, "Queued"),
                Int(root, "Joining"),
                Int(root, "EntityCount"),
                Int(root, "Framerate"),
                Int(root, "Memory"),
                Int(root, "Uptime"),
                Text(root, "GameTime"),
                Text(root, "Map"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int Int(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return (int)Math.Clamp(number, 0, int.MaxValue);
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? (int)Math.Clamp(parsed, 0, int.MaxValue)
            : 0;
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? UiTextSanitizer.Normalize(value.ToString(), UiTextSanitizer.MaxLabelLength)
            : string.Empty;
}

/// <summary>An F7 report relayed by Rust WebRCON.</summary>
public sealed record PlayerReport(
    DateTime ReceivedAt,
    string ReporterName,
    string ReporterId,
    string TargetName,
    string TargetId,
    string Subject,
    string Message,
    string Kind)
{
    public string TimeText => ReceivedAt.ToString("HH:mm");

    public string Summary =>
        $"{(ReporterName.Length > 0 ? ReporterName : "A player")} reported " +
        $"{(TargetName.Length > 0 ? TargetName : TargetId.Length > 0 ? TargetId : "a player")}" +
        (Kind.Length > 0 ? $" ({Kind})" : string.Empty) +
        (Subject.Length > 0 ? $": {Subject}" : string.Empty);

    public static PlayerReport Parse(string json)
    {
        string Read(JsonElement root, params string[] names)
        {
            foreach (var name in names)
                foreach (var property in root.EnumerateObject())
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return UiTextSanitizer.Normalize(property.Value.ToString(), UiTextSanitizer.MaxLabelLength);
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return new PlayerReport(
                    DateTime.Now,
                    Read(root, "PlayerName", "ReporterName"),
                    Read(root, "PlayerId", "ReporterId"),
                    Read(root, "TargetName"),
                    Read(root, "TargetId"),
                    Read(root, "Subject"),
                    Read(root, "Message"),
                    Read(root, "Type"));
        }
        catch (JsonException) { }
        return new PlayerReport(DateTime.Now, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, UiTextSanitizer.Normalize(json, UiTextSanitizer.MaxLabelLength), string.Empty);
    }
}
