using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SloomyTweaks.App.Controls;

/// <summary>
/// ON/OFF switch. Its visual state comes only from <see cref="IsOn"/> (bound one-way to the verified
/// state); clicking runs the bound command and never flips the switch by itself.
/// </summary>
public sealed class SwitchButton : Button
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(SwitchButton), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(SwitchButton), new FrameworkPropertyMetadata(false));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }
}

/// <summary>
/// Responsive grid: as many equal columns as fit (minimum width <see cref="MinColumnWidth"/>, at most
/// <see cref="MaxColumns"/>). Items in the same row share the row height so cards line up.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(420.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private int _columns = 1;
    private double _columnWidth;
    private readonly List<double> _rowHeights = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        _columns = Math.Clamp((int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing)), 1, Math.Max(1, MaxColumns));
        _columnWidth = Math.Max(0, (width - Spacing * (_columns - 1)) / _columns);

        _rowHeights.Clear();
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(_columnWidth, double.PositiveInfinity));
            int row = i / _columns;
            if (row >= _rowHeights.Count) _rowHeights.Add(0);
            _rowHeights[row] = Math.Max(_rowHeights[row], visible[i].DesiredSize.Height);
        }
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) c.Measure(new Size(0, 0));
        }
        double height = _rowHeights.Sum() + Spacing * Math.Max(0, _rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double y = 0;
        for (int i = 0; i < visible.Count; i++)
        {
            int row = i / _columns, col = i % _columns;
            if (col == 0 && row > 0) y += _rowHeights[row - 1] + Spacing;
            double h = row < _rowHeights.Count ? _rowHeights[row] : visible[i].DesiredSize.Height;
            visible[i].Arrange(new Rect(col * (_columnWidth + Spacing), y, _columnWidth, h));
        }
        return finalSize;
    }
}

/// <summary>Asks Windows 11 DWM to draw rounded corners for a window (ignored on Windows 10).</summary>
public static class WindowCorners
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            int preference = DWMWCP_ROUND;
            try { _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        };
    }
}
