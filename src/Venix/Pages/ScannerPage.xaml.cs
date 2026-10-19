using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Venix.Core;
using Venix.Dialogs;
using Venix.Trainer;

namespace Venix.Pages;

/// <summary>One line of the scan result list (only the first <see cref="ScannerPage.MaxRows"/> hits get a row).</summary>
public class ScanRow : Observable
{
    string _current = "";
    public int Index { get; init; }
    public long Address { get; init; }
    public string AddressLabel { get; init; } = "";
    public string Previous { get; init; } = "";
    public string Current { get => _current; set => Set(ref _current, value); }
}

/// <summary>
/// Memory scanner: finds values in a running game (exact search or guided discovery by behaviour), lets the user
/// edit/freeze them, adds them to the game's cheat profile and looks for pointer chains so they survive a restart.
/// </summary>
public partial class ScannerPage : UserControl, IPage
{
    public const int MaxRows = 1000;

    readonly TrainerSession _session;
    readonly DispatcherTimer _autoAttach = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly ObservableCollection<ScanRow> _rows = new();
    readonly Dictionary<ScanType, RadioButton> _typeChips = new();
    readonly Dictionary<ScanMode, RadioButton> _modeChips = new();
    string? _attachError;

    MemoryScanner? _scanner;
    ScanResults? _results;
    MemorySnapshot? _snapshot;
    ScanType _type = ScanType.Int32;
    ScanMode _mode = ScanMode.Exact;
    CancellationTokenSource? _cts;
    bool _busy;

    DiscoveryKind _kind = DiscoveryKind.Health;
    DiscoveryEngine? _discovery;
    DiscoveryCandidate? _testing;
    byte[]? _testOriginal;
    double _testWritten;

    PointerMap? _pointerMap;
    Task<PointerMap>? _mapTask;

    public string Title => _session.Name;
    public string Subtitle => "Scanner de memória  ·  encontre e salve seus próprios cheats";
    public string NavKey => "cheats";

    public ScannerPage(TrainerSession session)
    {
        InitializeComponent();
        _session = session;
        GameTitle.Text = session.Name;
        ResultList.ItemsSource = _rows;
        CheatList.ItemsSource = session.Entries;
        BuildChips();
        UpdateCheatInfo();
        session.Entries.CollectionChanged += (_, _) => UpdateCheatInfo();
        _session.AttachedChanged += OnAttachedChanged;
        _autoAttach.Tick += (_, _) => TryAutoAttach();
        _live.Tick += (_, _) => RefreshLive();
        Loaded += (_, _) => { OnAttachedChanged(); _autoAttach.Start(); _live.Start(); TryAutoAttach(); };
        Unloaded += (_, _) => { _autoAttach.Stop(); _live.Stop(); _session.AttachedChanged -= OnAttachedChanged; };
    }

    /* ---------------------------------------------------------- chips */

    void BuildChips()
    {
        foreach (var t in new[] { ScanType.Int32, ScanType.Float, ScanType.Int16, ScanType.Byte, ScanType.Int64, ScanType.Double, ScanType.String })
        {
            var rb = Chip(ScanOptions.TypeLabel(t), "stype", t == _type);
            rb.Checked += (_, _) => { _type = t; UpdateInputs(); };
            _typeChips[t] = rb;
            TypeChips.Children.Add(rb);
        }
        foreach (var m in Enum.GetValues<ScanMode>())
        {
            var rb = Chip(ScanOptions.ModeLabel(m), "smodechip", m == _mode);
            rb.Checked += (_, _) => { _mode = m; UpdateInputs(); };
            _modeChips[m] = rb;
            ModeChips.Children.Add(rb);
        }
        foreach (var k in Enum.GetValues<DiscoveryKind>())
        {
            var rb = Chip(DiscoveryEngine.KindLabel(k), "skind", k == _kind);
            rb.Checked += (_, _) => _kind = k;
            KindChips.Children.Add(rb);
        }
        UpdateInputs();
    }

    RadioButton Chip(string text, string group, bool on) => new()
    {
        Style = (Style)FindResource("FilterChip"), Content = text, GroupName = group, IsChecked = on, Height = 30, Padding = new Thickness(13, 0, 13, 0), FontSize = 12.5
    };

    void UpdateInputs()
    {
        bool scanned = _results != null || _snapshot != null;
        foreach (var (m, rb) in _modeChips)
            rb.IsEnabled = !_busy && (scanned ? m != ScanMode.Unknown : !ScanOptions.NeedsPrevious(m));
        foreach (var rb in _typeChips.Values) rb.IsEnabled = !_busy && !scanned;
        if (!_modeChips[_mode].IsEnabled)
        {
            _mode = scanned ? ScanMode.Exact : ScanMode.Exact;
            _modeChips[_mode].IsChecked = true;
        }
        bool text = _type == ScanType.String;
        Value1.Visibility = text || ScanOptions.NeedsValue(_mode) ? Visibility.Visible : Visibility.Collapsed;
        Value1.Tag = text ? "texto" : _mode switch { ScanMode.IncreasedBy or ScanMode.DecreasedBy => "diferença", ScanMode.Between => "de", _ => "valor" };
        Value2.Visibility = !text && _mode == ScanMode.Between ? Visibility.Visible : Visibility.Collapsed;
        FirstScanBtn.IsEnabled = !_busy && !scanned;
        NextScanBtn.IsEnabled = !_busy && scanned;
        NewScanBtn.IsEnabled = !_busy && scanned;
        AlignedChk.IsEnabled = !scanned;
        WritableOnly.IsEnabled = !scanned;
    }

    void OnPanel(object sender, RoutedEventArgs e)
    {
        if (ManualPanel == null || AutoPanel == null) return;
        bool manual = (string)((RadioButton)sender).Tag == "manual";
        ManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        AutoPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
    }

    /* ---------------------------------------------------------- attach */

    void OnAttachedChanged()
    {
        var on = _session.IsAttached;
        Dot.Fill = (Brush)FindResource(on ? "Success" : "Accent");
        StateText.Text = on ? "Conectado" : "Aguardando o jogo";
        StatusText.Text = on ? _session.Status
            : _attachError ?? (string.IsNullOrEmpty(_session.ProcessHint)
                ? "Abra o jogo e clique em \"Escolher processo\"."
                : $"Abra o jogo — o Venix conecta sozinho em {_session.ProcessHint}.");
        DetachBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && (_scanner == null || _scanner.Game != _session.Game)) _scanner = new MemoryScanner(_session.Game!);
        if (!on)
        {
            _cts?.Cancel();
            _scanner = null;
            _pointerMap = null;
            _mapTask = null;
            ClearScan();
            ResetDiscoveryUi();
        }
        Notice.Text = _session.ProfileWarning ?? "Somente para jogos offline / single-player. O Venix lê e altera a memória do jogo diretamente; nunca use em jogos online.";
        UpdateCheatInfo();
    }

    void TryAutoAttach()
    {
        if (_session.IsAttached) return;
        var p = _session.FindTargetProcess();
        if (p != null) Attach(p);
    }

    void Attach(Process p)
    {
        try
        {
            _session.Attach(p);
            _attachError = null;
            var ready = _session.ResolvedDiscovered.Count();
            MainWindow.Instance.Toast(ready > 0 ? $"Conectado a {p.ProcessName}  ·  {ready} cheat{(ready == 1 ? "" : "s")} salvo{(ready == 1 ? "" : "s")} pronto{(ready == 1 ? "" : "s")}" : $"Conectado a {p.ProcessName}");
        }
        catch (Exception ex)
        {
            _attachError = ex.Message;
            _autoAttach.Stop();
        }
        OnAttachedChanged();
    }

    void OnPick(object sender, RoutedEventArgs e) => MainWindow.Instance.ShowModal(new ProcessPickerDialog(AttachTo));

    /// <summary>Connects to a process the user picked and remembers its exe for the next auto-attach.</summary>
    public void AttachTo(Process p)
    {
        _session.ProcessHint = p.ProcessName + ".exe";
        if (_session.Profile != null && string.IsNullOrEmpty(_session.Profile.Exe)) _session.Profile.Exe = _session.ProcessHint;
        _autoAttach.Start();
        Attach(p);
    }

    void OnDetach(object sender, RoutedEventArgs e)
    {
        _session.Detach();
        _autoAttach.Stop();
        OnAttachedChanged();
    }

    void OnCloseSession(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        TrainerHub.Close(_session);
        MainWindow.Instance.Back();
    }

    bool EnsureAttached()
    {
        if (_session.IsAttached && _scanner != null) return true;
        MainWindow.Instance.Toast("Abra o jogo primeiro e conecte o scanner a ele.");
        return false;
    }

    /* ---------------------------------------------------------- manual scan */

    ScanOptions? BuildOptions()
    {
        var o = new ScanOptions { Type = _type, Mode = _mode, Aligned = AlignedChk.IsChecked == true, WritableOnly = WritableOnly.IsChecked == true };
        if (_type == ScanType.String)
        {
            if (string.IsNullOrEmpty(Value1.Text)) { MainWindow.Instance.Toast("Digite o texto a procurar."); return null; }
            o.Text = Value1.Text;
            o.Mode = ScanMode.Exact;
            return o;
        }
        if (ScanOptions.NeedsValue(_mode))
        {
            var v = MemoryScanner.ParseValue(_type, Value1.Text);
            if (v == null) { MainWindow.Instance.Toast("Digite um valor válido para esse tipo."); return null; }
            o.Value = v.Value;
            o.FloatTolerance = ScanOptions.ToleranceFor(Value1.Text);
            if (_mode == ScanMode.Between)
            {
                var v2 = MemoryScanner.ParseValue(_type, Value2.Text);
                if (v2 == null) { MainWindow.Instance.Toast("Digite o limite superior."); return null; }
                o.Value2 = v2.Value;
            }
        }
        return o;
    }

    void OnValueKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_results != null || _snapshot != null) OnNextScan(sender, e); else OnFirstScan(sender, e);
    }

    async void OnFirstScan(object sender, RoutedEventArgs e)
    {
        if (!EnsureAttached() || _busy) return;
        var o = BuildOptions();
        if (o == null) return;
        var scanner = _scanner!;
        await RunScan(async (progress, ct) =>
        {
            if (o.Mode == ScanMode.Unknown) _snapshot = await Task.Run(() => scanner.Snapshot(o, progress, ct), ct);
            else _results = await Task.Run(() => scanner.FirstScan(o, progress, ct), ct);
        }, ScanProgress, CancelScanBtn);
        ShowResults(o);
    }

    async void OnNextScan(object sender, RoutedEventArgs e)
    {
        if (!EnsureAttached() || _busy) return;
        var o = BuildOptions();
        if (o == null) return;
        var scanner = _scanner!;
        var prev = _results;
        var snap = _snapshot;
        await RunScan(async (progress, ct) =>
        {
            var r = await Task.Run(() => scanner.NextScan(o, prev, snap, progress, ct), ct);
            _results = r;
            _snapshot = null;
        }, ScanProgress, CancelScanBtn);
        ShowResults(o);
    }

    void OnNewScan(object sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        ClearScan();
    }

    void OnCancelScan(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void ClearScan()
    {
        _results = null;
        _snapshot = null;
        _rows.Clear();
        ResultCount.Text = "";
        ResultFooter.Text = "";
        ResultEmpty.Visibility = Visibility.Visible;
        UpdateInputs();
    }

    /// <summary>Runs a scan off the UI thread with progress, cancel button and error toasts.</summary>
    async Task<bool> RunScan(Func<IProgress<ScanProgress>, CancellationToken, Task> work, ProgressBar bar, Button cancelBtn)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _busy = true;
        UpdateInputs();
        bar.Value = 0;
        bar.Visibility = Visibility.Visible;
        cancelBtn.Visibility = Visibility.Visible;
        StartBtn.IsEnabled = false;
        int lastPct = -1;
        var progress = new Progress<ScanProgress>(p =>
        {
            int pct = (int)p.Percent;
            if (pct == lastPct) return;
            lastPct = pct;
            bar.Value = pct;
            if (bar == ScanProgress) ResultCount.Text = p.Hits > 0 ? $"{Format.Number(p.Hits)} até agora…" : $"{pct}%";
            else AutoStatus.Text = $"Analisando a memória… {pct}%";
        });
        bool ok = false;
        try { await work(progress, cts.Token); ok = true; }
        catch (OperationCanceledException) { MainWindow.Instance.Toast("Scan cancelado."); }
        catch (ScanLimitException ex) { MainWindow.Instance.Toast(ex.Message, true); }
        catch (Exception ex) { MainWindow.Instance.Toast("Falha no scan: " + ex.Message, true); }
        finally
        {
            _busy = false;
            bar.Visibility = Visibility.Collapsed;
            cancelBtn.Visibility = Visibility.Collapsed;
            StartBtn.IsEnabled = true;
            UpdateInputs();
        }
        return ok;
    }

    void ShowResults(ScanOptions o)
    {
        _rows.Clear();
        if (_snapshot != null)
        {
            long n = _snapshot.Candidates(o.Step);
            ResultCount.Text = $"{Format.Number(n)} candidatos";
            ResultFooter.Text = $"Foto da memória tirada ({Format.Bytes(_snapshot.TotalBytes)}). Mude o valor no jogo e use \"Aumentou\", \"Diminuiu\", \"Mudou\" ou \"Não mudou\" para filtrar.";
            ResultEmpty.Visibility = Visibility.Collapsed;
            UpdateInputs();
            return;
        }
        if (_results == null) { ClearScan(); return; }
        var r = _results;
        int shown = Math.Min(MaxRows, r.Count);
        for (int i = 0; i < shown; i++)
            _rows.Add(new ScanRow { Index = i, Address = r.Addresses[i], AddressLabel = _scanner?.Game.Describe((nint)r.Addresses[i]) ?? r.Addresses[i].ToString("X"), Previous = r.Format(i), Current = r.Format(i) });
        ResultCount.Text = r.Count == 1 ? "1 endereço" : $"{Format.Number(r.Count)} endereços";
        ResultFooter.Text = r.Count > shown ? $"Mostrando os primeiros {Format.Number(shown)} de {Format.Number(r.Count)}. Mude o valor no jogo e faça o próximo scan para refinar." : r.Count == 0 ? "Nenhum endereço. Clique em \"Novo scan\" e tente outro tipo ou outro valor." : "";
        ResultEmpty.Visibility = Visibility.Collapsed;
        UpdateInputs();
    }

    void RefreshLive()
    {
        if (_scanner == null || _rows.Count == 0 || ManualPanel.Visibility != Visibility.Visible || _busy) return;
        var r = _results;
        if (r == null) return;
        var game = _scanner.Game;
        if (game.HasExited) return;
        foreach (var row in _rows)
        {
            var b = game.TryRead((nint)row.Address, r.ValueSize);
            row.Current = b != null ? MemoryScanner.FormatValue(r.Type, b) : "??";
        }
    }

    void OnResultDouble(object sender, MouseButtonEventArgs e)
    {
        if (ResultList.SelectedItem is ScanRow row && _results != null) AddCheat(row.Address, _results.Type, null);
    }

    void OnAddResult(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ScanRow row && _results != null) AddCheat(row.Address, _results.Type, null);
    }

    /* ---------------------------------------------------------- discovered cheats */

    void AddCheat(long address, ScanType type, string? suggestedName)
    {
        if (!EnsureAttached()) return;
        var game = _scanner!.Game;
        var n = _session.Entries.Count + 1;
        MainWindow.Instance.ShowModal(new PromptDialog("Nome do cheat", $"Valor {ScanOptions.TypeLabel(type)} em {game.Describe((nint)address)}. O Venix salva no perfil de {_session.Name} e tenta achar um ponteiro para reconectar depois.",
            suggestedName ?? $"Cheat {n}", "Adicionar", name =>
            {
                var c = new SavedCheat { Name = name, Type = MemoryScanner.ToVariableType(type), LastAddress = address.ToString("X") };
                var mod = game.Modules.FirstOrDefault(m => address >= m.Base && address < m.Base + m.Size);
                if (mod != null) { c.Module = mod.Name; c.ModuleOffset = address - mod.Base; }
                var entry = _session.AddDiscovered(c, address);
                MainWindow.Instance.Toast(mod != null ? $"\"{name}\" adicionado (endereço estático, sobrevive ao reinício)" : $"\"{name}\" adicionado  ·  procurando ponteiro…");
                if (mod == null) _ = FindPointersAsync(entry, quiet: true);
            }));
    }

    void UpdateCheatInfo()
    {
        var all = _session.Entries.Where(e => e.Saved != null).ToList();
        CheatEmpty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CheatInfo.Text = all.Count == 0 ? "" : all.Count == 1 ? "1 salvo" : $"{all.Count} salvos";
        Counts.Children.Clear();
        AddCount(all.Count == 1 ? "1 cheat salvo" : $"{all.Count} cheats salvos");
        var persistent = all.Count(e => e.IsPersistent);
        if (persistent > 0) AddCount(persistent == all.Count ? "todos sobrevivem ao reinício" : $"{persistent} sobrevive{(persistent == 1 ? "" : "m")} ao reinício");
        var ready = _session.IsAttached ? all.Count(e => _session.Address(e) != null) : 0;
        ActivateAllBtn.Visibility = ready > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivateAllBtn.Content = ready == 1 ? "Ativar o cheat salvo" : $"Ativar {ready} cheats salvos";
    }

    void AddCount(string text) =>
        Counts.Children.Add(new Border { Style = (Style)FindResource("Chip"), Child = new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)FindResource("Text.2") } });

    async void OnActivateAll(object sender, RoutedEventArgs e)
    {
        if (!EnsureAttached()) return;
        var n = await _session.ActivateDiscoveredAsync();
        MainWindow.Instance.Toast(n == 0 ? "Nenhum cheat pôde ser ativado." : n == 1 ? "1 cheat ativado e congelado" : $"{n} cheats ativados e congelados");
    }

    static TrainerEntry? Row(object sender) => (sender as FrameworkElement)?.DataContext as TrainerEntry;

    async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || Row(sender) is not { } row) return;
        var want = cb.IsChecked == true;
        cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, row.IsActive);
        if (!_session.IsAttached) { MainWindow.Instance.Toast("Abra o jogo primeiro."); return; }
        await _session.SetActiveAsync(row, want);
        cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, row.IsActive);
        if (row.HasError) MainWindow.Instance.Toast(row.Error!, true);
    }

    void OnCheatValueKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb || Row(sender) is not { } row) return;
        if (string.IsNullOrWhiteSpace(tb.Text)) return;
        try { _session.WriteValue(row, tb.Text); row.Error = null; tb.Text = ""; }
        catch (Exception ex) { row.Error = ex.Message; }
    }

    void OnRemoveCheat(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) _session.RemoveDiscovered(row);
    }

    async void OnFindPointer(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) await FindPointersAsync(row, quiet: false);
    }

    /// <summary>Builds the pointer map once per attach and looks for static chains to the entry's current address.</summary>
    async Task FindPointersAsync(TrainerEntry entry, bool quiet)
    {
        if (!EnsureAttached() || entry.Saved == null) return;
        var addr = _session.Address(entry);
        if (addr == null) { MainWindow.Instance.Toast("Endereço desconhecido agora; conecte ao jogo e refaça a busca."); return; }
        var scanner = _scanner!;
        var game = scanner.Game;
        var mod = game.Modules.FirstOrDefault(m => addr >= m.Base && addr < m.Base + m.Size);
        if (mod != null)
        {
            entry.Saved.Module = mod.Name;
            entry.Saved.ModuleOffset = addr - mod.Base;
            entry.ResolvedHow = "estático";
            _session.SaveProfile();
            UpdateCheatInfo();
            if (!quiet) MainWindow.Instance.Toast("Endereço estático: já sobrevive ao reinício do jogo.");
            return;
        }
        entry.ResolvedHow = "procurando ponteiro…";
        try
        {
            if (_pointerMap == null)
            {
                _mapTask ??= Task.Run(() => PointerMap.Build(scanner, null, CancellationToken.None));
                _pointerMap = await _mapTask;
                if (_scanner != scanner) return; // detached meanwhile
            }
            var map = _pointerMap;
            var chains = await Task.Run(() => new PointerScanner(game, map).Find(addr.Value, CancellationToken.None));
            if (chains.Count == 0)
            {
                entry.ResolvedHow = "endereço desta execução";
                if (!quiet) MainWindow.Instance.Toast("Nenhum ponteiro estático encontrado para esse valor (até 4 níveis). O cheat vale só nesta execução.", true);
                else MainWindow.Instance.Toast($"\"{entry.Description}\": nenhum ponteiro encontrado; vale só nesta execução.");
                return;
            }
            entry.Saved.Module = chains[0].Module;
            entry.Saved.PointerChains = chains.Take(10).Select(c => c.Offsets.ToList()).ToList();
            entry.ResolvedHow = $"ponteiro ({chains[0].Levels} nível{(chains[0].Levels == 1 ? "" : "is")})";
            _session.SaveProfile();
            UpdateCheatInfo();
            MainWindow.Instance.Toast($"\"{entry.Description}\": {chains.Count} cadeia{(chains.Count == 1 ? "" : "s")} de ponteiro salva{(chains.Count == 1 ? "" : "s")}  ·  {chains[0].Label}");
        }
        catch (ScanLimitException ex) { entry.ResolvedHow = ""; MainWindow.Instance.Toast(ex.Message, true); }
        catch (Exception ex) { entry.ResolvedHow = ""; MainWindow.Instance.Toast("Falha ao procurar ponteiro: " + ex.Message, true); }
    }

    /* ---------------------------------------------------------- smart discovery */

    void ResetDiscoveryUi()
    {
        _discovery = null;
        _testing = null;
        StepCard.Visibility = Visibility.Collapsed;
        CandidatePanel.Visibility = Visibility.Collapsed;
        TestCard.Visibility = Visibility.Collapsed;
        RestartBtn.Visibility = Visibility.Collapsed;
        StartBtn.Visibility = Visibility.Visible;
        AutoStatus.Text = "";
        CandidateList.ItemsSource = null;
        foreach (RadioButton rb in KindChips.Children) rb.IsEnabled = true;
    }

    async void OnStartDiscovery(object sender, RoutedEventArgs e)
    {
        if (!EnsureAttached() || _busy) return;
        var engine = new DiscoveryEngine(_scanner!, _kind) { WritableOnly = WritableOnly.IsChecked == true };
        _discovery = engine;
        foreach (RadioButton rb in KindChips.Children) rb.IsEnabled = false;
        StartBtn.Visibility = Visibility.Collapsed;
        RestartBtn.Visibility = Visibility.Visible;
        CandidatePanel.Visibility = Visibility.Collapsed;
        TestCard.Visibility = Visibility.Collapsed;
        var ok = await RunScan(engine.StartAsync, AutoProgress, CancelAutoBtn);
        if (!ok) { ResetDiscoveryUi(); return; }
        AutoStatus.Text = $"Foto da memória tirada: {Format.Number(engine.TotalCandidates)} candidatos. Agora siga o passo abaixo.";
        ShowStep();
    }

    void OnRestartDiscovery(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ResetDiscoveryUi();
    }

    void ShowStep()
    {
        var d = _discovery;
        if (d == null) return;
        StepCard.Visibility = Visibility.Visible;
        StepButtons.Children.Clear();
        var step = d.CurrentStep;
        if (d.Kind != DiscoveryKind.Custom && step != null)
        {
            StepLabel.Text = $"Passo {d.StepIndex + 1} de {d.Steps.Count}";
            StepTitle.Text = step.Title;
            StepText.Text = step.Instruction;
            if (step.IsWait) StepButton($"Esperar {step.WaitSeconds} s", () => Apply(ScanMode.Unchanged, step.WaitSeconds), primary: true);
            else StepButton("Continuar", () => Apply(step.Mode, 0), primary: true);
            if (step.Mode == ScanMode.Increased) StepButton("Perdi de novo", () => Apply(ScanMode.Decreased, 0));
            StepButton("Pular", () => { d.Steps.RemoveAt(d.StepIndex); ShowStep(); }, link: true);
        }
        else
        {
            StepLabel.Text = d.StepsApplied == 0 ? "Primeiro passo" : $"{d.StepsApplied} passo{(d.StepsApplied == 1 ? "" : "s")} feito{(d.StepsApplied == 1 ? "" : "s")}";
            StepTitle.Text = d.Kind == DiscoveryKind.Custom ? "Faça algo no jogo e diga o que aconteceu" : "Quer refinar mais?";
            StepText.Text = "Mude o valor no jogo (ou não mude) e clique no que aconteceu com ele. Repita até sobrar poucos candidatos.";
            StepButton("Aumentou", () => Apply(ScanMode.Increased, 0), primary: true);
            StepButton("Diminuiu", () => Apply(ScanMode.Decreased, 0), primary: true);
            StepButton("Mudou", () => Apply(ScanMode.Changed, 0));
            StepButton("Não mudou", () => Apply(ScanMode.Unchanged, 0));
            StepButton("Esperar 3 s sem mudar", () => Apply(ScanMode.Unchanged, 3));
        }
        if (d.StepsApplied > 0) StepButton("Ver candidatos", ShowCandidates, link: true);
    }

    void StepButton(string text, Action action, bool primary = false, bool link = false)
    {
        Button b = link
            ? new Button { Style = (Style)FindResource("Btn.Link"), Content = text, Foreground = (Brush)FindResource("Ink.Text"), Opacity = 0.75, Margin = new Thickness(6, 0, 10, 8), VerticalAlignment = VerticalAlignment.Center }
            : new Button { Style = (Style)FindResource(primary ? "Btn.Light" : "Btn.Frost"), Content = text, Height = 36, Padding = new Thickness(16, 0, 16, 0), FontSize = 12.5, Margin = new Thickness(0, 0, 8, 8) };
        b.Click += (_, _) => action();
        StepButtons.Children.Add(b);
    }

    async void Apply(ScanMode mode, int waitSeconds)
    {
        var d = _discovery;
        if (d == null || !EnsureAttached() || _busy) return;
        TestCard.Visibility = Visibility.Collapsed;
        if (waitSeconds > 0) AutoStatus.Text = $"Aguardando {waitSeconds} s… não faça nada no jogo.";
        var ok = await RunScan((p, ct) => waitSeconds > 0 ? d.ApplyWaitAsync(waitSeconds, p, ct) : d.ApplyAsync(mode, p, ct), AutoProgress, CancelAutoBtn);
        if (!ok || _discovery != d) return;
        long n = d.TotalCandidates;
        var byType = string.Join("  ·  ", d.CountsByType.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => $"{ScanOptions.TypeLabel(kv.Key)} {Format.Number(kv.Value)}"));
        AutoStatus.Text = n == 0 ? "Nenhum candidato sobrou. Clique em Recomeçar e tente de novo (ou escolha \"Personalizado\")."
                        : $"{Format.Number(n)} candidato{(n == 1 ? "" : "s")} restante{(n == 1 ? "" : "s")}   ({byType})";
        ShowStep();
        if (n > 0 && n <= 5000) ShowCandidates();
        else if (n > 5000) CandidatePanel.Visibility = Visibility.Collapsed;
    }

    void ShowCandidates()
    {
        var d = _discovery;
        if (d == null) return;
        if (d.TotalCandidates > 2_000_000) { MainWindow.Instance.Toast("Ainda há candidatos demais para classificar. Faça mais um passo."); return; }
        var list = d.Rank(20);
        CandidateList.ItemsSource = list;
        CandidatePanel.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CandidateInfo.Text = list.Count == 0 ? "" : $"top {list.Count} de {Format.Number(d.TotalCandidates)}";
        if (list.Count == 0) MainWindow.Instance.Toast("Nenhum candidato plausível. Tente mais um passo ou recomece.");
    }

    void OnTestCandidate(object sender, RoutedEventArgs e)
    {
        if (!EnsureAttached() || _discovery == null || (sender as FrameworkElement)?.DataContext is not DiscoveryCandidate c) return;
        if (_testing != null && _testOriginal != null) _discovery.Restore(_testing, _testOriginal);
        var original = _discovery.TestWrite(c, out var written);
        if (original == null) { MainWindow.Instance.Toast("Não foi possível escrever nesse endereço."); return; }
        _testing = c;
        _testOriginal = original;
        _testWritten = written;
        var label = c.Type is ScanType.Float or ScanType.Double ? written.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : written.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TestText.Text = $"Troquei {c.ValueLabel} por {label} em {c.AddressLabel}. Olhe o jogo: {DiscoveryEngine.KindLabel(_kind).ToLowerInvariant()} virou {label}?  (Se o jogo só mostra ao mudar de tela, dê um passo nele.)";
        TestCard.Visibility = Visibility.Visible;
        TestCard.BringIntoView();
    }

    void OnTestYes(object sender, RoutedEventArgs e)
    {
        var c = _testing;
        if (c == null) return;
        TestCard.Visibility = Visibility.Collapsed;
        _testing = null;
        _testOriginal = null;
        AddCheat(c.Address, c.Type, DiscoveryEngine.KindLabel(_kind));
    }

    void OnTestNo(object sender, RoutedEventArgs e)
    {
        if (_testing != null && _testOriginal != null && _discovery != null) _discovery.Restore(_testing, _testOriginal);
        TestCard.Visibility = Visibility.Collapsed;
        if (CandidateList.ItemsSource is List<DiscoveryCandidate> list && _testing != null)
        {
            // drop the rejected candidate from the list so the next best moves up
            var rest = list.Where(x => x != _testing).ToList();
            CandidateList.ItemsSource = rest;
            if (rest.Count == 0) MainWindow.Instance.Toast("Acabaram os candidatos desta lista. Faça mais um passo para refinar.");
        }
        _testing = null;
        _testOriginal = null;
    }

    void OnAddCandidate(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DiscoveryCandidate c) AddCheat(c.Address, c.Type, DiscoveryEngine.KindLabel(_kind));
    }
}
