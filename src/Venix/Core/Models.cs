using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Venix.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class AppSettings
{
    public string DownloadDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string Theme { get; set; } = "Light";
    public int SpeedLimitMbps { get; set; }
    /// <summary>TCP/UDP port the torrent engine listens on. Picked once at random and kept.</summary>
    public int ListenPort { get; set; }
    public bool SeedAfterDownload { get; set; }
    public bool StartOnLibrary { get; set; }
    /// <summary>Public trackers appended to every magnet (many list magnets carry none).</summary>
    public bool AddPublicTrackers { get; set; } = true;
    public List<string> Trackers { get; set; } = DefaultTrackers.ToList();
    /// <summary>Last queries typed in the title-bar search (newest first, no duplicates), shown as "Buscas recentes".</summary>
    public List<string> SearchHistory { get; set; } = new();
    /// <summary>Navigate the launcher with a connected controller (D-pad/stick, A, B, LB/RB...).</summary>
    public bool GamepadNavigation { get; set; } = true;
    /// <summary>Id of the controller used for navigation and game sessions; null = automatic (Xbox first).</summary>
    public string? GamepadPrimaryId { get; set; }
    /// <summary>Present non-Xbox controllers to games as a virtual Xbox 360 pad while they run (needs ViGEmBus).</summary>
    public bool GamepadLaunchSupport { get; set; } = true;

    public static readonly string[] DefaultTrackers =
    {
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.tiny-vps.com:6969/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://open.tracker.cl:1337/announce",
        "udp://tracker.theoks.net:6969/announce",
        "http://tracker.opentrackr.org:1337/announce",
        "https://tracker.tamersunion.org:443/announce"
    };
}

public class DownloadSource : Observable
{
    private string _status = "ok";
    private string? _error;
    private int _count;
    private string _name = "";
    private DateTime? _syncedAt;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string? Url { get; set; }
    public string? FilePath { get; set; }
    /// <summary>A .json from the sources folder next to Venix.exe; managed by that folder.</summary>
    public bool FromConfig { get; set; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public int Count { get => _count; set => Set(ref _count, value); }
    public string Status { get => _status; set { Set(ref _status, value); Raise(nameof(StatusLabel)); } }
    public string? Error { get => _error; set { Set(ref _error, value); Raise(nameof(HasError)); Raise(nameof(CanOpenInBrowser)); } }
    public DateTime? SyncedAt { get => _syncedAt; set { Set(ref _syncedAt, value); Raise(nameof(SyncedLabel)); } }

    [JsonIgnore] public bool HasError => !string.IsNullOrEmpty(Error);
    [JsonIgnore] public bool CanOpenInBrowser => HasError && Url != null;
    [JsonIgnore] public string StatusLabel => Status switch { "syncing" => "Sincronizando", "error" => "Erro", _ => "Sincronizada" };
    [JsonIgnore] public string SyncedLabel => SyncedAt is { } d ? $"Atualizada {d.ToLocalTime():dd/MM/yyyy HH:mm}" : "Nunca sincronizada";
    [JsonIgnore] public string Origin => Url ?? FilePath ?? "";
    [JsonIgnore] public string OriginLabel => FromConfig ? "pasta sources" : FilePath != null ? "arquivo importado" : "adicionada manualmente";
}

public class LibraryGame : Observable
{
    private string? _exe;
    private long _playtime;
    private DateTime? _lastPlayed;
    private string? _installPath;

    public string Id { get; set; } = "";
    public int? AppId { get; set; }
    public string Title { get; set; } = "";
    public string? Exe { get => _exe; set { Set(ref _exe, value); Raise(nameof(HasExe)); } }
    public string? InstallPath { get => _installPath; set => Set(ref _installPath, value); }
    public long PlaytimeSeconds { get => _playtime; set { Set(ref _playtime, value); Raise(nameof(PlaytimeLabel)); } }
    public DateTime? LastPlayed { get => _lastPlayed; set => Set(ref _lastPlayed, value); }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Repository path of the cheat table linked to this game.</summary>
    public string? CheatTable { get; set; }
    public bool CheatsOnLaunch { get; set; }

    [JsonIgnore] public bool HasExe => !string.IsNullOrEmpty(Exe);
    [JsonIgnore] public string PlaytimeLabel => Format.Playtime(PlaytimeSeconds);
}

public class DownloadItem : Observable
{
    private string _status = "connecting";
    private double _progress;
    private long _size;
    private long _speed;
    private int _peers;
    private int _seeds;
    private int _connections;
    private TimeSpan? _eta;
    private string? _name;
    private string? _error;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = "";
    public string Uri { get; set; } = "";
    public string GameId { get; set; } = "";
    public int? AppId { get; set; }
    public string GameTitle { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string FileSize { get; set; } = "";
    public string SavePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public string Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(StatusLabel)); Raise(nameof(IsActive)); Raise(nameof(IsPaused));
            Raise(nameof(IsComplete)); Raise(nameof(CanPause)); Raise(nameof(CanResume));
        }
    }
    public double Progress { get => _progress; set { if (Set(ref _progress, value)) { Raise(nameof(ProgressLabel)); Raise(nameof(ProgressPercent)); } } }
    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(SizeLabel)); } }
    public string? Name { get => _name; set => Set(ref _name, value); }
    public string? Error { get => _error; set => Set(ref _error, value); }

    [JsonIgnore] public long Speed { get => _speed; set { if (Set(ref _speed, value)) Raise(nameof(SpeedLabel)); } }
    [JsonIgnore] public int Peers { get => _peers; set { if (Set(ref _peers, value)) Raise(nameof(PeersLabel)); } }
    [JsonIgnore] public int Seeds { get => _seeds; set { if (Set(ref _seeds, value)) Raise(nameof(PeersLabel)); } }
    [JsonIgnore] public int Connections { get => _connections; set { if (Set(ref _connections, value)) Raise(nameof(PeersLabel)); } }
    /// <summary>"12 conectados · 44 seeds / 60 peers": connected count first, so a swarm with peers but no connections is visible.</summary>
    [JsonIgnore] public string PeersLabel => $"{Connections} conectados · {Seeds} seeds / {Peers} peers";
    [JsonIgnore] public TimeSpan? Eta { get => _eta; set { if (Set(ref _eta, value)) Raise(nameof(EtaLabel)); } }

    [JsonIgnore] public bool IsActive => Status is "connecting" or "metadata" or "downloading" or "hashing";
    [JsonIgnore] public bool IsPaused => Status is "paused" or "error";
    [JsonIgnore] public bool IsComplete => Status is "complete" or "seeding";
    [JsonIgnore] public bool CanPause => IsActive;
    [JsonIgnore] public bool CanResume => IsPaused;
    [JsonIgnore] public double ProgressPercent => Progress * 100;
    [JsonIgnore] public string ProgressLabel => (Progress * 100).ToString("0.0");
    [JsonIgnore] public string SpeedLabel => Format.Speed(Speed);
    [JsonIgnore] public string SizeLabel => Size > 0 ? Format.Bytes(Size) : (string.IsNullOrEmpty(FileSize) ? "—" : FileSize);
    [JsonIgnore] public string EtaLabel => Format.Eta(Eta);
    [JsonIgnore] public string StatusLabel => Status switch
    {
        "connecting" => "Conectando",
        "metadata" => "Obtendo metadados",
        "hashing" => "Verificando arquivos",
        "downloading" => "Baixando",
        "paused" => "Pausado",
        "cancelling" => "Cancelando",
        "seeding" => "Semeando",
        "complete" => "Concluído",
        "error" => "Erro",
        _ => Status
    };
}

/// <summary>One entry of a source JSON (Hydra-compatible format).</summary>
public class SourceEntry
{
    public string SourceId { get; init; } = "";
    public string SourceName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Clean { get; init; } = "";
    public string Norm { get; init; } = "";
    public List<string> Uris { get; init; } = new();
    public string? UploadDate { get; init; }
    public string FileSize { get; init; } = "";
    /// <summary>Full game, update, patch or fix — read from the title (see <see cref="EntryClassifier"/>).</summary>
    public EntryKind Kind { get; init; } = EntryKind.Game;
    /// <summary>Set by the download dialog so the option it was opened for starts checked (not part of the list data).</summary>
    public bool Preselected { get; set; }

    public bool IsGame => Kind == EntryKind.Game;
    /// <summary>"Jogo", "Atualização", "Patch", "Fix".</summary>
    public string KindLabel => EntryClassifier.Label(Kind);
    /// <summary>Section header when options are grouped by kind: "Jogo", "Atualizações", "Patches", "Fixes".</summary>
    public string Section => EntryClassifier.Section(Kind);
    public string DateLabel => DateTime.TryParse(UploadDate, out var d) ? d.ToString("dd/MM/yyyy") : "";
    public string Meta => string.Join("  ·  ", new[] { FileSize, SourceName, DateLabel }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>One game of the catalog: every full-game entry sharing a normalized name, plus the updates, patches and fixes linked to it.</summary>
public class CatalogGroup
{
    public string Key { get; init; } = "";
    public string Title { get; set; } = "";
    public List<string> Sources { get; } = new();
    /// <summary>Full-game download options.</summary>
    public int Count { get; set; }
    public int Updates { get; set; }
    public int Patches { get; set; }
    public int Fixes { get; set; }
    public string? Latest { get; set; }

    /// <summary>"3 atualizações · 1 patch · 1 fix", or "" when the game has none.</summary>
    public string ExtrasLabel => string.Join(" · ", new[]
    {
        Updates > 0 ? EntryClassifier.Count(EntryKind.Update, Updates) : null,
        Patches > 0 ? EntryClassifier.Count(EntryKind.Patch, Patches) : null,
        Fixes > 0 ? EntryClassifier.Count(EntryKind.Fix, Fixes) : null
    }.Where(s => s != null));
}

/// <summary>An update, patch or fix from the lists together with the catalog game it belongs to (null when no list has that game).</summary>
public class ExtraEntry
{
    public SourceEntry Entry { get; init; } = null!;
    public CatalogGroup? Game { get; init; }
    /// <summary>Name of the base game: the catalog title when linked, the cleaned entry title otherwise.</summary>
    public string GameTitle => Game?.Title ?? Entry.Clean;
}

public class SteamApp
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
}

public class SteamDetails
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string About { get; set; } = "";
    public List<string> Developers { get; set; } = new();
    public List<string> Publishers { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public string ReleaseDate { get; set; } = "";
    public int? Metacritic { get; set; }
    public int? Recommendations { get; set; }
    public List<string> Screenshots { get; set; } = new();
    public string? Controller { get; set; }
}
