using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Features.Analysis;

/// <summary>仅负责窄屏导航、焦点和诊断选择；计算与文件保存始终由模型协调。</summary>
public partial class AnalysisView : UserControl
{
    private AnalysisWorkspace? _observed;
    public AnalysisView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && DataContext is AnalysisWorkspace page)
            { if (page.ShowEditor) page.ShowEditor = false; else page.ShowTimeline = false; ApplyButton.Focus(); args.Handled = true; }
        };
    }
    private void Observe()
    {
        if (_observed is not null) _observed.PropertyChanged -= Changed;
        _observed = DataContext as AnalysisWorkspace;
        if (_observed is not null) _observed.PropertyChanged += Changed;
        LayoutPanes();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(AnalysisWorkspace.ShowTimeline)) LayoutPanes(); }
    private void LayoutPanes()
    {
        if (AnalysisPanes is null) return;
        var compact = Bounds.Width < 700; var timeline = _observed?.ShowTimeline == true;
        AnalysisPanes.ColumnDefinitions = new ColumnDefinitions(compact ? "*" : "230,*");
        GroupList.IsVisible = !compact || !timeline; TimelinePane.IsVisible = !compact || timeline;
        Grid.SetColumn(TimelinePane, compact ? 0 : 1);
    }
    private void ToggleEditor(object? sender, RoutedEventArgs args)
    { if (DataContext is AnalysisWorkspace page) { page.ShowEditor = !page.ShowEditor; if (page.ShowEditor) DslText.Focus(); else ApplyButton.Focus(); } }
    private void Back(object? sender, RoutedEventArgs args) { if (DataContext is AnalysisWorkspace page) page.ShowTimeline = false; }
    private void SelectDiagnostic(object? sender, SelectionChangedEventArgs args)
    {
        if (DiagnosticList.SelectedItem is not DslDiagnostic diagnostic) return;
        DslText.Focus(); DslText.SelectionStart = diagnostic.Span.Start; DslText.SelectionEnd = diagnostic.Span.Start + diagnostic.Span.Length;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == BoundsProperty) LayoutPanes(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Observe(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { if (_observed is not null) _observed.PropertyChanged -= Changed; _observed = null; base.OnDetachedFromVisualTree(e); }
}
