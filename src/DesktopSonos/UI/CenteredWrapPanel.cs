using System.Windows;
using System.Windows.Controls;

namespace DesktopSonos.UI;

/// <summary>
/// A wrap panel that centres every line, not just the block as a whole. WPF's own WrapPanel packs
/// each line to the left, so a row of buttons that spills onto a second line leaves that second
/// line hanging under the left edge however the panel itself is aligned.
/// </summary>
public sealed class CenteredWrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, widest = 0, totalHeight = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;

            if (lineWidth > 0 && lineWidth + size.Width > availableSize.Width)
            {
                widest = Math.Max(widest, lineWidth);
                totalHeight += lineHeight;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        widest = Math.Max(widest, lineWidth);
        totalHeight += lineHeight;

        // Never ask for more than was offered, or the panel pushes its neighbours off screen.
        return new Size(
            double.IsInfinity(availableSize.Width) ? widest : Math.Min(widest, availableSize.Width),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var line = new List<UIElement>();
        double lineWidth = 0, lineHeight = 0, y = 0;

        foreach (UIElement child in InternalChildren)
        {
            var size = child.DesiredSize;

            if (line.Count > 0 && lineWidth + size.Width > finalSize.Width)
            {
                ArrangeLine(line, lineWidth, lineHeight, y, finalSize.Width);
                y += lineHeight;
                line.Clear();
                lineWidth = 0;
                lineHeight = 0;
            }

            line.Add(child);
            lineWidth += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        ArrangeLine(line, lineWidth, lineHeight, y, finalSize.Width);
        return finalSize;
    }

    private static void ArrangeLine(List<UIElement> line, double lineWidth, double lineHeight,
        double y, double totalWidth)
    {
        var x = Math.Max(0, (totalWidth - lineWidth) / 2);
        foreach (var child in line)
        {
            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(x, y, width, lineHeight));
            x += width;
        }
    }
}
