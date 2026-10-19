using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Venix.Dialogs;

/// <summary>Single text input card ("name this cheat"). callback(text) runs only on confirm.</summary>
public class PromptDialog : UserControl
{
    public PromptDialog(string title, string text, string initial, string confirm, Action<string> callback)
    {
        Width = 460;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("T.H2"), FontSize = 22 });
        if (!string.IsNullOrEmpty(text))
            stack.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("T.Body"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Style = (Style)FindResource("Input"), Text = initial, Margin = new Thickness(0, 18, 0, 18) };
        stack.Children.Add(input);
        var ok = new Button { Style = (Style)FindResource("Btn.Primary"), Content = confirm, Margin = new Thickness(0, 0, 0, 8) };
        var cancel = new Button { Style = (Style)FindResource("Btn.Link"), Content = "Cancelar", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        void Confirm()
        {
            var v = input.Text.Trim();
            if (v.Length == 0) return;
            MainWindow.Instance.CloseModal();
            callback(v);
        }
        ok.Click += (_, _) => Confirm();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) Confirm(); };
        cancel.Click += (_, _) => MainWindow.Instance.CloseModal();
        stack.Children.Add(ok);
        stack.Children.Add(cancel);
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

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
