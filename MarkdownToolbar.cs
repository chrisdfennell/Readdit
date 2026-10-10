using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Skein;

/// <summary>Markdown formatting applied to a selection (the same rules as the post view's toolbar).</summary>
public static class MarkdownEdits
{
    /// <summary>The text after an edit, and what to select afterwards.</summary>
    public readonly record struct Result(string Text, int SelectionStart, int SelectionLength);

    /// <summary>
    /// Wraps the selection ("**bold**"), or unwraps it if it's already wrapped. With nothing
    /// selected, inserts an example to type over. Multi-line code becomes a fenced block.
    /// </summary>
    public static Result Wrap(string text, int start, int length, string before, string after, string example)
    {
        var selected = text.Substring(start, length);
        if (before == "`" && selected.Contains('\n'))
        {
            before = "```\n";
            after = "\n```";
        }
        var end = start + length;
        if (length > 0 && start >= before.Length && end + after.Length <= text.Length &&
            text.Substring(start - before.Length, before.Length) == before && text.Substring(end, after.Length) == after)
        {
            var unwrapped = text[..(start - before.Length)] + selected + text[(end + after.Length)..];
            return new Result(unwrapped, start - before.Length, length);
        }
        if (length == 0) selected = example;
        var wrapped = text[..start] + before + selected + after + text[end..];
        return new Result(wrapped, start + before.Length, selected.Length);
    }

    /// <summary>Adds (or removes, if every line has it) a line prefix: "> ", "- ", "1. ", "## ".</summary>
    public static Result PrefixLines(string text, int start, int length, string prefix)
    {
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var newline = text.IndexOf('\n', start + length);
        var lineEnd = newline < 0 ? text.Length : newline;
        var lines = text[lineStart..lineEnd].Split('\n');
        var numbered = prefix == "1. ";
        bool Has(string line) => numbered ? System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\. ") : line.StartsWith(prefix);
        var all = lines.All(Has);
        var result = lines.Select((line, i) => all
            ? (numbered ? System.Text.RegularExpressions.Regex.Replace(line, @"^\d+\. ", "") : line[prefix.Length..])
            : (numbered ? $"{i + 1}. " : prefix) + System.Text.RegularExpressions.Regex.Replace(line, @"^(> |- |\d+\. |#{1,6} )", ""));
        var joined = string.Join('\n', result);
        return new Result(text[..lineStart] + joined + text[lineEnd..], lineStart, joined.Length);
    }
}

/// <summary>
/// A row of formatting buttons for a Markdown text box (bold, italic, link, quote, code,
/// spoiler, lists, heading), plus Ctrl+B / Ctrl+I / Ctrl+K in that box.
/// </summary>
public sealed class MarkdownToolbar : WrapPanel
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(TextBox), typeof(MarkdownToolbar), new PropertyMetadata(null, OnTargetChanged));

    /// <summary>The text box the buttons format.</summary>
    public TextBox? Target
    {
        get => (TextBox?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public MarkdownToolbar()
    {
        Margin = new Thickness(0, 0, 0, 4);
        Add("B", "Bold (Ctrl+B)", () => Wrap("**", "**", "bold text"), FontWeights.Bold);
        Add("I", "Italic (Ctrl+I)", () => Wrap("*", "*", "italic text"), style: FontStyles.Italic);
        Add("S", "Strikethrough", () => Wrap("~~", "~~", "struck text"), decorations: TextDecorations.Strikethrough);
        Add("🔗", "Link (Ctrl+K)", Link);
        Add("❝", "Quote", () => Prefix("> "));
        Add("</>", "Code", () => Wrap("`", "`", "code"));
        Add("⚠", "Spoiler", () => Wrap(">!", "!<", "spoiler"));
        Add("^", "Superscript", () => Wrap("^(", ")", "superscript"));
        Add("•", "Bulleted list", () => Prefix("- "));
        Add("1.", "Numbered list", () => Prefix("1. "));
        Add("H", "Heading", () => Prefix("## "));
    }

    void Add(string label, string tip, Action action, FontWeight? weight = null, FontStyle? style = null,
        TextDecorationCollection? decorations = null)
    {
        var text = new TextBlock { Text = label, FontWeight = weight ?? FontWeights.SemiBold, FontStyle = style ?? FontStyles.Normal };
        if (decorations != null) text.TextDecorations = decorations;
        var button = new Button
        {
            Content = text,
            ToolTip = tip,
            MinWidth = 32,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 3, 0),
            Focusable = false, // the text box keeps focus (and its selection)
        };
        button.Click += (_, _) => action();
        Children.Add(button);
    }

    static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toolbar = (MarkdownToolbar)d;
        if (e.OldValue is TextBox old) old.PreviewKeyDown -= toolbar.OnTargetKeyDown;
        if (e.NewValue is TextBox target) target.PreviewKeyDown += toolbar.OnTargetKeyDown;
    }

    void OnTargetKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        Action? action = e.Key switch
        {
            Key.B => () => Wrap("**", "**", "bold text"),
            Key.I => () => Wrap("*", "*", "italic text"),
            Key.K => Link,
            _ => null,
        };
        if (action == null) return;
        e.Handled = true;
        action();
    }

    void Apply(Func<string, int, int, MarkdownEdits.Result> edit)
    {
        if (Target is not { } box) return;
        // Text boxes use \r\n; the edits work on \n and are put back the same way.
        var text = box.Text.Replace("\r\n", "\n");
        var before = box.Text[..box.SelectionStart].Replace("\r\n", "\n").Length;
        var length = box.Text.Substring(box.SelectionStart, box.SelectionLength).Replace("\r\n", "\n").Length;
        var result = edit(text, before, length);
        var crlf = box.Text.Contains("\r\n") || box.AcceptsReturn;
        string Out(string s) => crlf ? s.Replace("\n", "\r\n") : s;
        box.Text = Out(result.Text);
        box.SelectionStart = Out(result.Text[..result.SelectionStart]).Length;
        box.SelectionLength = Out(result.Text.Substring(result.SelectionStart, result.SelectionLength)).Length;
        box.Focus();
    }

    void Wrap(string before, string after, string example) =>
        Apply((text, start, length) => MarkdownEdits.Wrap(text, start, length, before, after, example));

    void Prefix(string prefix) => Apply((text, start, length) => MarkdownEdits.PrefixLines(text, start, length, prefix));

    void Link()
    {
        var dialog = new TextInputWindow("Add a link", "Link address:", "https://") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (!Uri.TryCreate(dialog.Value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
        Wrap("[", $"]({uri.AbsoluteUri})", "link text");
    }
}
