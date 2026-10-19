using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeForge.Studio.Views;

/// <summary>Renders text with fenced ``` code blocks as monospace panels. Other Markdown is shown as plain text.</summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(string.Empty, (d, _) => ((MarkdownView)d).Rebuild()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static IReadOnlyList<(bool IsCode, string Language, string Content)> Parse(string text)
    {
        var parts = new List<(bool, string, string)>();
        var sb = new StringBuilder();
        var inCode = false;
        var lang = "";
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCode)
                {
                    Flush(false, "");
                    lang = raw.TrimStart()[3..].Trim();
                    inCode = true;
                }
                else
                {
                    Flush(true, lang);
                    inCode = false;
                }
                continue;
            }
            sb.Append(raw).Append('\n');
        }
        Flush(inCode, lang); // unterminated fence while streaming still renders as code
        return parts;

        void Flush(bool code, string l)
        {
            var s = sb.ToString().TrimEnd('\n');
            sb.Clear();
            if (s.Length > 0 || code) parts.Add((code, l, s));
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var (isCode, lang, content) in Parse(Text))
        {
            if (!isCode)
            {
                Children.Add(new TextBox
                {
                    Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                    Foreground = (Brush)FindResource("TextBrush"), Padding = new Thickness(0),
                    Margin = new Thickness(0, 2, 0, 2), FontSize = 14
                });
                continue;
            }
            var panel = new StackPanel();
            if (lang.Length > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = lang, FontSize = 11, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 4)
                });
            panel.Children.Add(new TextBox
            {
                Text = content, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("TextBrush"), Padding = new Thickness(0),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            });
            Children.Add(new Border
            {
                Background = (Brush)FindResource("CodeBgBrush"), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 4), Child = panel
            });
        }
    }
}
