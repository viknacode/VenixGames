using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Venix.Dialogs;

/// <summary>Two-choice confirmation card. callback(true) = primary (destructive) action.</summary>
public class ConfirmDialog : UserControl
{
    public ConfirmDialog(string title, string text, string primary, string secondary, Func<bool, Task> callback)
    {
        Width = 460;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("T.H2"), FontSize = 22 });
        stack.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("T.Body"), Margin = new Thickness(0, 10, 0, 24) });

        var danger = new Button { Style = (Style)FindResource("Btn.Primary"), Content = primary, Background = (Brush)FindResource("Danger"), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
        var keep = new Button { Style = (Style)FindResource("Btn"), Content = secondary, Margin = new Thickness(0, 0, 0, 8) };
        var cancel = new Button { Style = (Style)FindResource("Btn.Link"), Content = "Voltar", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        danger.Click += async (_, _) => { MainWindow.Instance.CloseModal(); await callback(true); };
        keep.Click += async (_, _) => { MainWindow.Instance.CloseModal(); await callback(false); };
        cancel.Click += (_, _) => MainWindow.Instance.CloseModal();
        stack.Children.Add(danger);
        stack.Children.Add(keep);
        stack.Children.Add(cancel);

        Content = new Border
        {
            Style = (Style)FindResource("Glass"),
            Background = (Brush)FindResource("Glass.Strong"),
            Effect = (System.Windows.Media.Effects.Effect)FindResource("Shadow.Lift"),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(30, 28, 30, 22),
            Child = stack
        };
    }
}
