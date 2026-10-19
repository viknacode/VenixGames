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

/// <summary>Runs a cheat table inside Venix: attach to the game, toggle scripts, edit and freeze values.</summary>
public partial class TrainerPage : UserControl, IPage
{
    readonly TrainerSession _session;
    readonly DispatcherTimer _autoAttach = new() { Interval = TimeSpan.FromSeconds(1.5) };
    string? _attachError;

    public string Title => _session.Name;
    public string Subtitle => "Trainer  ·  aplica a tabela direto na memória do jogo";
    public string NavKey => "cheats";

    public TrainerPage(TrainerSession session)
    {
        InitializeComponent();
        _session = session;
        List.ItemsSource = session.Entries;
        var t = session.Table;
        var all = session.Entries;
        TableInfo.Text = System.IO.Path.GetFileName(session.TablePath ?? session.Name);
        var scripts = all.Count(e => e.IsScript);
        var values = all.Count(e => e.IsValue);
        AddCount(scripts == 1 ? "1 script" : $"{scripts} scripts");
        AddCount(values == 1 ? "1 valor" : $"{values} valores");
        var unsupported = all.Count(e => e.IsUnsupported);
        if (unsupported > 0) AddCount(unsupported == 1 ? "1 não suportado" : $"{unsupported} não suportados");
        Notice.Text = t.HasTableLua
            ? "Esta tabela tem um script Lua global, que o Venix não executa. Os scripts e valores comuns funcionam normalmente; o que depender desse Lua aparece como não suportado."
            : "Use apenas em modo offline / single-player. Tabelas feitas para outra versão do jogo podem não encontrar o código (o Venix avisa e não altera nada nesse caso).";

        _session.AttachedChanged += UpdateState;
        _autoAttach.Tick += (_, _) => TryAutoAttach();
        Loaded += (_, _) => { UpdateState(); _autoAttach.Start(); TryAutoAttach(); };
        Unloaded += (_, _) => { _autoAttach.Stop(); _session.AttachedChanged -= UpdateState; };
    }

    void AddCount(string text) =>
        Counts.Children.Add(new Border { Style = (Style)FindResource("Chip"), Child = new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)FindResource("Text.2") } });

    void UpdateState()
    {
        var on = _session.IsAttached;
        Dot.Fill = (Brush)FindResource(on ? "Success" : "Accent");
        StateText.Text = on ? "Conectado" : "Aguardando o jogo";
        StatusText.Text = on ? _session.Status
            : _attachError ?? (string.IsNullOrEmpty(_session.ProcessHint)
                ? "Abra o jogo e clique em \"Escolher processo\"."
                : $"Abra o jogo — o Venix conecta sozinho em {_session.ProcessHint}.");
        DetachBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    void TryAutoAttach()
    {
        if (_session.IsAttached) return;
        var p = _session.FindTargetProcess();
        if (p == null) return;
        Attach(p);
    }

    void Attach(Process p)
    {
        try
        {
            _session.Attach(p);
            _attachError = null;
            MainWindow.Instance.Toast($"Conectado a {p.ProcessName}");
        }
        catch (Exception ex)
        {
            _attachError = ex.Message;
            _autoAttach.Stop(); // don't retry an access error every 1.5 s
        }
        UpdateState();
    }

    void OnPick(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.ShowModal(new ProcessPickerDialog(p => { _session.ProcessHint = p.ProcessName + ".exe"; _autoAttach.Start(); Attach(p); }));

    void OnDetach(object sender, RoutedEventArgs e)
    {
        _session.Detach();
        _autoAttach.Stop(); // stay detached until the user picks again
        UpdateState();
    }

    void OnCloseSession(object sender, RoutedEventArgs e)
    {
        TrainerHub.Close(_session);
        MainWindow.Instance.Back();
    }

    /* ---------------------------------------------------------- rows */

    static TrainerEntry? Row(object sender) => (sender as FrameworkElement)?.DataContext as TrainerEntry;

    async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || Row(sender) is not { } row) return;
        var want = cb.IsChecked == true;
        cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, row.IsActive); // keeps the OneWay binding alive
        if (!_session.IsAttached) { MainWindow.Instance.Toast("Abra o jogo primeiro — o Venix conecta automaticamente."); return; }
        await _session.SetActiveAsync(row, want);
        cb.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, row.IsActive);
        if (row.HasError) MainWindow.Instance.Toast(row.Error!, true);
    }

    void OnExpand(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        row.Expanded = !row.Expanded;
        void Apply(TrainerEntry parent, bool visible)
        {
            foreach (var c in parent.Children)
            {
                c.Visible = visible;
                Apply(c, visible && c.Expanded);
            }
        }
        Apply(row, row.Visible && row.Expanded);
    }

    void OnValueKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb || Row(sender) is not { } row) return;
        Write(row, tb.Text);
        tb.Text = "";
    }

    void OnOption(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string label } b) return;
        // the button lives inside the options list; walk up to the row's data context
        DependencyObject d = b;
        while (d != null && (d as FrameworkElement)?.DataContext is not TrainerEntry) d = VisualTreeHelper.GetParent(d);
        if ((d as FrameworkElement)?.DataContext is TrainerEntry row) Write(row, label);
    }

    void Write(TrainerEntry row, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { _session.WriteValue(row, text); row.Error = null; }
        catch (Exception ex) { row.Error = ex.Message; }
    }
}
