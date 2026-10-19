using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Venix.Core;

/// <summary>Persistent app state: settings, sources, library and download history.</summary>
public class AppStore
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Venix");
    static readonly string FilePath = Path.Combine(DataDir, "venix.json");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public AppSettings Settings { get; set; } = new();
    public ObservableCollection<DownloadSource> Sources { get; set; } = new();
    public ObservableCollection<LibraryGame> Library { get; set; } = new();
    public ObservableCollection<DownloadItem> Downloads { get; set; } = new();

    public event Action? LibraryChanged;
    public event Action<string>? RunningChanged;
    readonly HashSet<string> _running = new();
    public bool IsRunning(string id) => _running.Contains(id);

    public static AppStore Load()
    {
        Directory.CreateDirectory(DataDir);
        if (!File.Exists(FilePath)) return new AppStore();
        try
        {
            var store = JsonSerializer.Deserialize<AppStore>(File.ReadAllText(FilePath), Json) ?? new AppStore();
            store.Settings.Migrate();
            return store;
        }
        catch (Exception ex)
        {
            // Never silently start from scratch: keep the unreadable file for recovery and say why.
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            try { File.AppendAllText(Path.Combine(DataDir, "error.log"), $"[{DateTime.Now:s}] venix.json ilegível, copiado para venix.json.bad: {ex}\n\n"); } catch { }
            return new AppStore();
        }
    }

    readonly object _saveLock = new();
    public void Save()
    {
        string json;
        lock (_saveLock) json = JsonSerializer.Serialize(this, Json);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, true);
    }

    CancellationTokenSource? _debounce;
    public void SaveSoon()
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        Task.Delay(400, token).ContinueWith(t => { if (!t.IsCanceled) Application.Current.Dispatcher.Invoke(Save); }, TaskScheduler.Default);
    }

    /* ---------------------------------------------------------- library */

    public LibraryGame? Find(string id) => Library.FirstOrDefault(g => g.Id == id);

    public LibraryGame AddToLibrary(string id, int? appId, string title)
    {
        var g = Find(id);
        if (g != null) return g;
        g = new LibraryGame { Id = id, AppId = appId, Title = title };
        Library.Add(g);
        SaveSoon();
        LibraryChanged?.Invoke();
        return g;
    }

    public void RemoveFromLibrary(string id)
    {
        var g = Find(id);
        if (g == null) return;
        Library.Remove(g);
        SaveSoon();
        LibraryChanged?.Invoke();
    }

    public void Launch(LibraryGame g)
    {
        if (!g.HasExe || !File.Exists(g.Exe)) throw new InvalidOperationException("Defina o executável do jogo primeiro.");
        if (_running.Contains(g.Id)) return;
        var p = Process.Start(new ProcessStartInfo(g.Exe!) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(g.Exe) });
        g.LastPlayed = DateTime.UtcNow;
        SaveSoon();
        LibraryChanged?.Invoke();
        if (p == null) return;
        _running.Add(g.Id);
        RunningChanged?.Invoke(g.Id);
        var started = DateTime.UtcNow;
        p.EnableRaisingEvents = true;
        p.Exited += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            g.PlaytimeSeconds += (long)(DateTime.UtcNow - started).TotalSeconds;
            _running.Remove(g.Id);
            RunningChanged?.Invoke(g.Id);
            LibraryChanged?.Invoke();
            SaveSoon();
        });
    }
}
