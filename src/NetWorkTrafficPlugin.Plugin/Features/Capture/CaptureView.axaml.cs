using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NetWorkTrafficPlugin.Features.Capture;

/// <summary>View 只管理可见寿命和布局。100ms 时钟自身位于 UI 线程，不向 Dispatcher 排入无界委托。</summary>
public partial class CaptureView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CaptureWorkspace? _model;
    public CaptureView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) =>
        {
            if (_model is null) return;
            await _model.RefreshAsync();
            if (_model.Follow && _model.Rows.Count > 0) TrafficList.ScrollIntoView(_model.Rows[0]);
            if (_model.Follow && _model.HasStream) BodyScroll.ScrollToEnd();
        };
        DataContextChanged += (_, _) => BindModel();
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && _model is not null) { _model.BackCommand.Execute(null); e.Handled = true; } };
    }
    private void BindModel()
    {
        if (_model is not null) _model.PropertyChanged -= ModelChanged;
        _model = DataContext as CaptureWorkspace;
        if (_model is not null) _model.PropertyChanged += ModelChanged;
        LayoutPanes();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(CaptureWorkspace.ShowDetails)) LayoutPanes(); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && MainGrid is not null) LayoutPanes();
    }
    private void LayoutPanes()
    {
        if (_model is null || MainGrid is null) return;
        var compact = Bounds.Width < 720; _model.IsCompact = compact;
        MainGrid.ColumnDefinitions = compact ? new("*") : new("*,8,1.2*");
        Grid.SetColumn(DetailsPane, compact ? 0 : 2);
        ListPane.IsVisible = !compact || !_model.ShowDetails;
        DetailsPane.IsVisible = !compact || _model.ShowDetails;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); BindModel(); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop(); if (_model is not null) _model.PropertyChanged -= ModelChanged;
        base.OnDetachedFromVisualTree(e);
    }
}
