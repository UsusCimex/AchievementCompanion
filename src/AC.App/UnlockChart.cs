using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AC.Core;

namespace AC.App;

/// <summary>
/// Unlocks over time as a step line: a step is a day with unlocks, as high as the number unlocked that day.
/// The line runs on to today, so flat stretches are breaks; the longest one gets a label. Hovering shows a day.
/// </summary>
public sealed class UnlockChart : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = Register(nameof(Days), typeof(IReadOnlyList<UnlockDay>));
    public static readonly DependencyProperty TotalProperty = Register(nameof(Total), typeof(int), 0);
    public static readonly DependencyProperty StrokeProperty = Register(nameof(Stroke), typeof(Brush));
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush), typeof(Brush));
    public static readonly DependencyProperty TextBrushProperty = Register(nameof(TextBrush), typeof(Brush));
    public static readonly DependencyProperty LabelBrushProperty = Register(nameof(LabelBrush), typeof(Brush));
    public static readonly DependencyProperty SurfaceBrushProperty = Register(nameof(SurfaceBrush), typeof(Brush));

    public IReadOnlyList<UnlockDay>? Days { get => (IReadOnlyList<UnlockDay>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }
    /// <summary>Achievements in the game: the top of the scale.</summary>
    public int Total { get => (int)GetValue(TotalProperty); set => SetValue(TotalProperty, value); }
    /// <summary>The line, the dots and, faintly, the area under the line.</summary>
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    /// <summary>Axis labels and the break label.</summary>
    public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    /// <summary>The label at the end of the line.</summary>
    public Brush? LabelBrush { get => (Brush?)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    /// <summary>The background under the chart: rings of it keep the dots apart from the line.</summary>
    public Brush? SurfaceBrush { get => (Brush?)GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }

    private const double DotRadius = 4, RingWidth = 2;
    /// <summary>A break shorter than this is ordinary play, not worth a label.</summary>
    private const int LongBreakDays = 7;

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative };
    private readonly List<Point> _points = new();
    private int _hover = -1;

    private static DependencyProperty Register(string name, Type type, object? fallback = null) =>
        DependencyProperty.Register(name, type, typeof(UnlockChart),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? 0 : available.Height);

    protected override void OnRender(DrawingContext dc)
    {
        _points.Clear();
        // Transparent rather than empty: the whole area takes the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Days is not { Count: > 0 } days || Total <= 0 || Stroke is not { } stroke || GridBrush is not { } grid ||
            TextBrush is not { } text || LabelBrush is not { } label || SurfaceBrush is not { } surface) return;

        var culture = CultureInfo.GetCultureInfo(Loc.Language);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = days[0].Day;
        var last = today > days[^1].Day ? today : days[^1].Day;
        int span = Math.Max(1, last.DayNumber - first.DayNumber);

        var end = Text($"{days[^1].Total}/{Total}", 12, label, FontWeights.SemiBold);
        var scale = new[] { 0, 50, 100 }.Select(p => (Share: p / 100.0, Mark: Text($"{p}%", 11, text))).ToList();
        double left = scale.Max(s => s.Mark.Width) + 10, right = ActualWidth - end.Width - 10;
        double top = 22, bottom = ActualHeight - 22;
        if (right - left < 60 || bottom - top < 40) return;
        double X(DateOnly d) => left + (right - left) * (d.DayNumber - first.DayNumber) / span;
        double Y(int n) => bottom - (bottom - top) * n / Total;
        // A hairline on a pixel center stays one pixel wide.
        static double Crisp(double v) => Math.Round(v) + 0.5;

        var gridPen = new Pen(grid, 1);
        foreach (var (share, mark) in scale)
        {
            double y = Crisp(bottom - (bottom - top) * share);
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            dc.DrawText(mark, new Point(left - 10 - mark.Width, y - mark.Height / 2));
        }

        double free = double.NegativeInfinity;
        foreach (var (day, format) in Ticks(first, last))
        {
            var mark = Text(day.ToString(format, culture), 11, text);
            double x = Math.Clamp(X(day) - mark.Width / 2, 0, ActualWidth - mark.Width);
            if (x < free) continue;
            dc.DrawText(mark, new Point(x, bottom + 6));
            free = x + mark.Width + 12;
        }

        int level = days[0].Total - days[0].Unlocked.Count;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(new Point(X(first), Y(level)), false, false);
            a.BeginFigure(new Point(X(first), bottom), true, true);
            a.LineTo(new Point(X(first), Y(level)), false, false);
            foreach (var d in days)
            {
                foreach (var p in new[] { new Point(X(d.Day), Y(level)), new Point(X(d.Day), Y(d.Total)) })
                {
                    l.LineTo(p, true, true);
                    a.LineTo(p, false, false);
                }
                level = d.Total;
                _points.Add(new Point(X(d.Day), Y(d.Total)));
            }
            l.LineTo(new Point(X(last), Y(level)), true, true);
            a.LineTo(new Point(X(last), Y(level)), false, false);
            a.LineTo(new Point(X(last), bottom), false, false);
        }
        line.Freeze();
        area.Freeze();

        dc.PushOpacity(0.1);
        dc.DrawGeometry(stroke, null, area);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);

        if (_hover >= 0 && _hover < _points.Count)
        {
            double x = Crisp(_points[_hover].X);
            dc.PushOpacity(0.5);
            dc.DrawLine(new Pen(text, 1), new Point(x, top), new Point(x, bottom));
            dc.Pop();
        }
        for (int i = 0; i < _points.Count; i++)
        {
            double r = i == _hover ? DotRadius + 1.5 : DotRadius;
            dc.DrawEllipse(surface, null, _points[i], r + RingWidth, r + RingWidth);
            dc.DrawEllipse(stroke, null, _points[i], r, r);
        }

        dc.DrawText(end, new Point(X(last) + 8, Y(level) - end.Height / 2));

        if (UnlockHistory.LongestBreak(days, today) is { } gap && gap.To.DayNumber - gap.From.DayNumber >= LongBreakDays)
        {
            var mark = Text(Loc.Plural("history.break", gap.To.DayNumber - gap.From.DayNumber), 11, text);
            double x1 = X(gap.From), x2 = X(gap.To);
            if (mark.Width + 16 <= x2 - x1)
                dc.DrawText(mark, new Point((x1 + x2 - mark.Width) / 2, Y(days.Last(d => d.Day <= gap.From).Total) - mark.Height - 6));
        }
    }

    /// <summary>Axis dates: days for a short history, first days of months for a long one.</summary>
    private static IEnumerable<(DateOnly Day, string Format)> Ticks(DateOnly first, DateOnly last)
    {
        int span = last.DayNumber - first.DayNumber;
        if (span <= 62)
        {
            int step = span <= 7 ? 1 : span <= 21 ? 3 : 7;
            for (var d = first; d <= last; d = d.AddDays(step)) yield return (d, Loc.T("history.axisDay"));
            yield break;
        }
        int months = span <= 190 ? 1 : span <= 400 ? 2 : span <= 800 ? 3 : span <= 1600 ? 6 : 12;
        var m = new DateOnly(first.Year, first.Month, 1).AddMonths(1);
        while ((m.Month - 1) % months != 0) m = m.AddMonths(1);
        for (; m <= last; m = m.AddMonths(months)) yield return (m, Loc.T("history.axisMonth"));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Days is not { } days || _points.Count != days.Count || _points.Count == 0) return;
        double x = e.GetPosition(this).X;
        int nearest = 0;
        for (int i = 1; i < _points.Count; i++)
            if (Math.Abs(_points[i].X - x) < Math.Abs(_points[nearest].X - x)) nearest = i;
        if (nearest == _hover) return;
        _hover = nearest;
        InvalidateVisual();
        _tip.PlacementTarget = this;
        _tip.Content = Describe(days[nearest]);
        _tip.HorizontalOffset = _points[nearest].X + 14;
        _tip.VerticalOffset = _points[nearest].Y + 14;
        _tip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.IsOpen = false;
        InvalidateVisual();
    }

    private StackPanel Describe(UnlockDay day)
    {
        var panel = new StackPanel { MaxWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = day.Day.ToString(Loc.T("history.date"), CultureInfo.GetCultureInfo(Loc.Language)),
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"+{day.Unlocked.Count}: {string.Join(", ", day.Unlocked.Select(a => a.Name))}",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("overlay.progress", day.Total, Total, Math.Round(100.0 * day.Total / Total)),
            Foreground = TextBrush,
            Margin = new Thickness(0, 2, 0, 0),
        });
        return panel;
    }

    private FormattedText Text(string s, double size, Brush brush, FontWeight? weight = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
