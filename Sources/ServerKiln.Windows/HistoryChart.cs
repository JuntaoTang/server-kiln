using System.Globalization;

namespace ServerKiln.Windows;

public sealed class HistoryChart : FrameworkElement
{
    private HistoryPoint[] points = [];
    private DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-1), until = DateTimeOffset.UtcNow;
    public void SetPoints(HistoryPoint[] value, DateTimeOffset start, DateTimeOffset end)
    { points = value; since = start; until = end; InvalidateVisual(); }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 100 || ActualHeight < 100) return;
        var textBrush = (Brush)FindResource("MutedBrush");
        var border = new Pen((Brush)FindResource("BorderBrush"), 1);
        var bounds = new Rect(42, 12, ActualWidth - 60, ActualHeight - 50);
        void Text(string text, double x, double y, Brush? brush = null) => context.DrawText(new FormattedText(text,
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11,
            brush ?? textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        for (var n = 0; n <= 4; n++)
        {
            var y = bounds.Top + n * bounds.Height / 4;
            context.DrawLine(border, new Point(bounds.Left, y), new Point(bounds.Right, y));
            Text($"{100 - n * 25}%", 0, y - 7);
        }
        for (var n = 0; n <= 4; n++)
        {
            var time = since + (until - since) * (n / 4.0);
            var x = bounds.Left + bounds.Width * n / 4;
            Text((until - since).TotalDays > 1.1 ? time.LocalDateTime.ToString("MM-dd") : time.LocalDateTime.ToString("HH:mm"), x - 15, bounds.Bottom + 12);
        }
        if (points.Length == 0)
        {
            Text(Localizer.Current["NoHistory"], bounds.Left + 8, bounds.Top + bounds.Height / 2);
            return;
        }
        var series = new (Func<HistoryPoint, double?> Value, Color Color, string Label)[]
        {
            (p => p.Utilization / 100, Color.FromRgb(131, 197, 163), "GPU"),
            (p => p.MemoryFraction, Color.FromRgb(229, 160, 105), Localizer.Current["Vram"]),
            (p => p.Cpu, Color.FromRgb(126, 171, 235), "CPU"),
            (p => p.Ram, Color.FromRgb(190, 157, 225), Localizer.Current["Ram"])
        };
        context.PushClip(new RectangleGeometry(bounds));
        foreach (var (getValue, color, _) in series)
        {
            var pen = new Pen(new SolidColorBrush(color), 1.8);
            Point? previous = null;
            DateTimeOffset? previousTime = null;
            foreach (var sample in points)
            {
                if (getValue(sample) is not { } value) { previous = null; previousTime = null; continue; }
                var p = new Point(bounds.Left + (sample.Time - since).TotalSeconds / (until - since).TotalSeconds * bounds.Width,
                    bounds.Bottom - Math.Clamp(value, 0, 1) * bounds.Height);
                // Leave gaps for missing collection periods rather than inventing continuous data.
                var gap = Math.Max(180, (until - since).TotalSeconds / 360);
                if (previous is { } last && (sample.Time - previousTime!.Value).TotalSeconds <= gap) context.DrawLine(pen, last, p);
                else context.DrawEllipse(pen.Brush, null, p, 2.3, 2.3);
                previous = p; previousTime = sample.Time;
            }
        }
        context.Pop();
        var legendX = bounds.Left;
        foreach (var (_, color, label) in series)
        { Text("● " + label, legendX, bounds.Top + 3, new SolidColorBrush(color)); legendX += 100; }
    }
}
