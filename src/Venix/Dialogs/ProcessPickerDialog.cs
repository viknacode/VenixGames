using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Venix.Core;

namespace Venix.Dialogs;

/// <summary>Lists windowed processes so the user can pick the game to attach to.</summary>
public class ProcessPickerDialog : UserControl
{
    public ProcessPickerDialog(Action<Process> onPick)
    {
        Width = 520;
        var list = new StackPanel();
        var self = Environment.ProcessId;
        var procs = Process.GetProcesses()
            .Where(p => { try { return p.Id != self && p.MainWindowHandle != 0; } catch { return false; } })
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var p in procs)
        {
            string title;
            try { title = p.MainWindowTitle; } catch { title = ""; }
            var row = new Button { Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 0, 6), FocusVisualStyle = null, Tag = p };
            row.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
                <ControlTemplate TargetType="Button" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <Border x:Name="bd" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" CornerRadius="14" Padding="14,10" Background="{DynamicResource Glass.Soft}" BorderBrush="{DynamicResource Line}" BorderThickness="1">
                    <ContentPresenter />
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bd" Property="Background" Value="{DynamicResource Glass.Hover}" /></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
                """);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = $"{p.ProcessName}.exe", FontSize = 13.5, Foreground = (Brush)FindResource("Text") });
            sp.Children.Add(new TextBlock { Text = $"{title}  ·  PID {p.Id}", FontSize = 11.5, Foreground = (Brush)FindResource("Text.3"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Content = sp;
            row.Click += (_, _) => { MainWindow.Instance.CloseModal(); onPick((Process)row.Tag); };
            list.Children.Add(row);
        }

        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "Escolher processo", Style = (Style)FindResource("T.H2"), FontSize = 22 });
        root.Children.Add(new TextBlock { Text = "Programas com janela aberta agora. Escolha o jogo.", Style = (Style)FindResource("T.Body"), Margin = new Thickness(0, 6, 0, 16) });
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var cancel = new Button { Style = (Style)FindResource("Btn"), Content = "Cancelar", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        cancel.Click += (_, _) => MainWindow.Instance.CloseModal();
        root.Children.Add(cancel);

        Content = new Border
        {
            Style = (Style)FindResource("Glass"),
            Background = (Brush)FindResource("Glass.Strong"),
            Effect = (System.Windows.Media.Effects.Effect)FindResource("Shadow.Lift"),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(28, 26, 28, 22),
            Child = root
        };
    }
}
