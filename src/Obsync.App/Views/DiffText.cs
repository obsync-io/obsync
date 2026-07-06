using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Obsync.App.ViewModels;

namespace Obsync.App.Views;

/// <summary>
/// Renders a <see cref="DiffRow"/>'s segments as inline runs on a TextBlock, giving intra-line word
/// changes a stronger background tint. An attached property (rather than an ItemsControl per line)
/// keeps row templates cheap enough for virtualized diffs of thousands of lines.
/// </summary>
public static class DiffText
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.RegisterAttached(
        "Row", typeof(DiffRow), typeof(DiffText), new PropertyMetadata(null, OnRowChanged));

    public static DiffRow? GetRow(TextBlock element) => (DiffRow?)element.GetValue(RowProperty);

    public static void SetRow(TextBlock element, DiffRow? value) => element.SetValue(RowProperty, value);

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        textBlock.Inlines.Clear();
        if (e.NewValue is not DiffRow row)
        {
            return;
        }

        var emphasis = row.Kind switch
        {
            DiffRowKind.Added => Application.Current?.TryFindResource("DiffAddedEmphasisBrush") as Brush,
            DiffRowKind.Deleted => Application.Current?.TryFindResource("DiffDeletedEmphasisBrush") as Brush,
            _ => null,
        };

        foreach (var segment in row.Segments)
        {
            var run = new Run(segment.Text);
            if (segment.IsEmphasized && emphasis is not null)
            {
                run.Background = emphasis;
            }

            if (row.IsStruck)
            {
                run.TextDecorations = TextDecorations.Strikethrough;
            }

            textBlock.Inlines.Add(run);
        }
    }
}
