using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;

namespace Launcher.App.Controls;

/// <summary>CommonMark parsed by Markdig, rendered as wrapping WPF content without a nested scroll area.</summary>
public sealed class MarkdownText : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarkdownText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty EnableMarkdownProperty = DependencyProperty.Register(nameof(EnableMarkdown), typeof(bool), typeof(MarkdownText), new PropertyMetadata(true, Changed));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool EnableMarkdown { get => (bool)GetValue(EnableMarkdownProperty); set => SetValue(EnableMarkdownProperty, value); }
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MarkdownText)d).Render();
    private void Render()
    {
        // Render before the virtualizing panel measures a recycled row. A deferred render
        // first measured every off-screen message at zero height, then moved the viewport.
        Children.Clear();
        if (string.IsNullOrEmpty(Text)) return;
        if (!EnableMarkdown) { var plain = TextBlock(); plain.Margin = new Thickness(0); plain.Text = Text; Children.Add(plain); return; }
        RenderBlocks(Markdown.Parse(Text, Pipeline), this);
    }
    private static TextBlock TextBlock()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, Margin = new Thickness(0, 0, 0, 6) };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return text;
    }
    private static void RenderBlocks(ContainerBlock blocks, Panel panel)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Markdig.Extensions.Tables.Table table:
                    RenderTable(table, panel); break;
                case HeadingBlock heading:
                    var title = TextBlock(); title.FontSize = heading.Level switch { 1 => 24, 2 => 21, _ => 17 };
                    title.LineHeight = double.NaN; title.FontWeight = FontWeights.SemiBold; title.Margin = new(0, 10, 0, 8);
                    RenderInlines(heading.Inline, title.Inlines); panel.Children.Add(title); break;
                case ParagraphBlock paragraph:
                    var text = TextBlock(); RenderInlines(paragraph.Inline, text.Inlines); panel.Children.Add(text); break;
                case CodeBlock code:
                    var codeText = TextBlock(); codeText.Text = code.Lines.ToString(); codeText.FontFamily = new FontFamily("Consolas"); codeText.FontSize = 13; codeText.Margin = new(0);
                    var frame = new Border { Child = codeText, Padding = new(12), CornerRadius = new(10), Margin = new(0, 4, 0, 10) };
                    frame.SetResourceReference(Border.BackgroundProperty, "InputBrush"); panel.Children.Add(frame); break;
                case QuoteBlock quote:
                    var quoted = new StackPanel(); RenderBlocks(quote, quoted);
                    var quoteFrame = new Border { Child = quoted, BorderThickness = new(2, 0, 0, 0), Padding = new(12, 2, 0, 0), Margin = new(0, 4, 0, 8) };
                    quoteFrame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); panel.Children.Add(quoteFrame); break;
                case ListBlock list:
                    var number = int.TryParse(Convert.ToString(list.OrderedStart, CultureInfo.InvariantCulture), out var start) ? start : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var row = new Grid(); row.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); row.ColumnDefinitions.Add(new());
                        var marker = TextBlock(); marker.Text = list.IsOrdered ? number++ + "." : "•"; row.Children.Add(marker);
                        var content = new StackPanel(); RenderBlocks(item, content); Grid.SetColumn(content, 1); row.Children.Add(content); panel.Children.Add(row);
                    }
                    break;
                case ThematicBreakBlock:
                    var rule = new Border { Height = 1, Margin = new(0, 8, 0, 12) }; rule.SetResourceReference(Border.BackgroundProperty, "AppleCardBorderBrush"); panel.Children.Add(rule); break;
                case HtmlBlock html:
                    var literal = TextBlock(); literal.Text = html.Lines.ToString(); panel.Children.Add(literal); break;
                case ContainerBlock container: RenderBlocks(container, panel); break;
            }
        }
    }
    private static void RenderTable(Markdig.Extensions.Tables.Table table, Panel panel)
    {
        var grid = new Grid { Margin = new(0, 6, 0, 12) };
        var rows = table.OfType<Markdig.Extensions.Tables.TableRow>().ToArray();
        var columns = rows.Select(r => r.Count).DefaultIfEmpty().Max();
        for (int col = 0; col < columns; col++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); var row = rows[rowIndex];
            for (int col = 0; col < row.Count; col++)
            {
                var content = new StackPanel(); RenderBlocks((Markdig.Extensions.Tables.TableCell)row[col], content);
                if (row.IsHeader) foreach (var text in content.Children.OfType<TextBlock>()) text.FontWeight = FontWeights.SemiBold;
                if (col < table.ColumnDefinitions.Count)
                    foreach (var text in content.Children.OfType<TextBlock>()) text.TextAlignment = table.ColumnDefinitions[col].Alignment switch { TableColumnAlign.Center => TextAlignment.Center, TableColumnAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
                var cell = new Border { Child = content, Padding = new(10, 8, 10, 2), BorderThickness = new(0, 0, 1, 1) };
                cell.SetResourceReference(Border.BorderBrushProperty, "AppleCardBorderBrush");
                if (row.IsHeader) cell.SetResourceReference(Border.BackgroundProperty, "InputBrush");
                Grid.SetRow(cell, rowIndex); Grid.SetColumn(cell, col); grid.Children.Add(cell);
            }
        }
        panel.Children.Add(grid);
    }
    private static void RenderInlines(ContainerInline? container, InlineCollection destination)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: destination.Add(new Run(literal.Content.ToString())); break;
                case LineBreakInline: destination.Add(new LineBreak()); break;
                case CodeInline code:
                    var run = new Run(code.Content) { FontFamily = new FontFamily("Consolas"), FontSize = 13 }; run.SetResourceReference(TextElement.BackgroundProperty, "InputBrush"); destination.Add(run); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic(); RenderInlines(emphasis, span.Inlines); destination.Add(span); break;
                case LinkInline link:
                    if (!link.IsImage && BrowserLink(link.Url) is { } hyperlink) { RenderInlines(link, hyperlink.Inlines); destination.Add(hyperlink); }
                    else RenderInlines(link, destination); // Images and HTML never fetch or execute remote content.
                    break;
                case AutolinkInline auto:
                    if (BrowserLink(auto.Url) is { } autoLink) { autoLink.Inlines.Add(new Run(auto.Url)); destination.Add(autoLink); }
                    else destination.Add(new Run(auto.Url));
                    break;
                case HtmlInline html: destination.Add(new Run(html.Tag)); break;
                case ContainerInline nested: RenderInlines(nested, destination); break;
            }
        }
    }
    private static Hyperlink? BrowserLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri }; link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        link.RequestNavigate += (_, e) => { e.Handled = true; try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { } };
        return link;
    }
}
