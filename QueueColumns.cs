using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BlenderRenderHeadless;

public sealed class QueueColumnWidth : DependencyObject
{
    public static readonly DependencyProperty WidthProperty = DependencyProperty.Register(nameof(Width), typeof(GridLength), typeof(QueueColumnWidth), new PropertyMetadata(new GridLength(100)));
    public GridLength Width { get => (GridLength)GetValue(WidthProperty); set => SetValue(WidthProperty, value); }
}

public partial class MainWindow
{
    private void QueueColumn_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string indexText, Parent: Grid header } || !int.TryParse(indexText, out var index)) return;
        var state = (QueueColumnWidth)Resources["QueueColumn" + index];
        state.Width = new GridLength(Math.Clamp(header.ColumnDefinitions[index].ActualWidth + e.HorizontalChange, 45, 1600));
        QueueTableGrid.MinWidth = Enumerable.Range(0, 9).Sum(i => { var width = ((QueueColumnWidth)Resources["QueueColumn" + i]).Width; return width.IsAbsolute ? width.Value : 100; }) + SystemParameters.VerticalScrollBarWidth + 6;
        e.Handled = true;
    }
}
