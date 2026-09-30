using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Features.Analysis;

/// <summary>每个虚拟化行只绘制一段；共同时间轴来自当前组范围，开放端不随UI时钟假装增长。</summary>
public sealed class TimelineStrip : Control
{
    public static readonly StyledProperty<AnalyzedRequest?> RecordProperty = AvaloniaProperty.Register<TimelineStrip, AnalyzedRequest?>(nameof(Record));
    public AnalyzedRequest? Record { get => GetValue(RecordProperty); set => SetValue(RecordProperty, value); }
    static TimelineStrip() => AffectsRender<TimelineStrip>(RecordProperty);
    public override void Render(DrawingContext context)
    {
        base.Render(context); if (Record is not { } row) return;
        var workspace = this.GetVisualAncestors().OfType<AnalysisView>().FirstOrDefault()?.DataContext as AnalysisWorkspace;
        if (workspace is null || workspace.Requests.Count == 0) return;
        var from = workspace.TimelineStart;
        var total = workspace.TimelineMilliseconds;
        var x = Math.Clamp((row.StartedUtc - from).TotalMilliseconds / total * Bounds.Width, 0, Bounds.Width);
        var duration = double.IsFinite(row.ElapsedMilliseconds) ? Math.Max(0, row.ElapsedMilliseconds) : 0;
        var width = Math.Min(Bounds.Width - x, Math.Max(2, duration / total * Bounds.Width));
        context.DrawLine(new Pen(Brushes.Gray, 1), new(0, 6), new(Bounds.Width, 6));
        context.FillRectangle(row.Error ? Brushes.IndianRed : Brushes.SteelBlue, new Rect(x, 2, width, 8));
        if (row.Transport == TransportState.Receiving) context.DrawEllipse(null, new Pen(Brushes.Orange, 1.5), new(x + width, 6), 3, 4);
    }
}
