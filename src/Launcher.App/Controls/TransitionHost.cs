using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Launcher.App.Controls;

// Cached pages are detached when inactive; only one presenter participates in layout/rendering.
public sealed class TransitionHost : ContentControl
{
    private readonly Dictionary<object, FrameworkElement> _views = new(ReferenceEqualityComparer.Instance);
    private readonly TranslateTransform _offset = new();
    private static readonly DependencyPropertyKey PresentedViewPropertyKey = DependencyProperty.RegisterReadOnly(nameof(PresentedView), typeof(FrameworkElement), typeof(TransitionHost), new PropertyMetadata(null));
    public static readonly DependencyProperty PresentedViewProperty = PresentedViewPropertyKey.DependencyProperty;
    public FrameworkElement? PresentedView => (FrameworkElement?)GetValue(PresentedViewProperty);
    public int CachedViewCount => _views.Count;
    public TransitionHost()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Stretch);
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(PresentedView)) { RelativeSource = RelativeSource.TemplatedParent });
        Template = new ControlTemplate(typeof(TransitionHost)) { VisualTree = presenter };
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        RenderTransform = _offset;
        Loaded += (_, _) => MotionPolicy.Changed += ResetMotion;
        Unloaded += (_, _) => { MotionPolicy.Changed -= ResetMotion; ResetMotion(); };
    }
    public void Preload(IEnumerable<object> pages)
    {
        foreach (var page in pages)
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            { if (IsLoaded && !Dispatcher.HasShutdownStarted) ResolveView(page); }));
    }
    private FrameworkElement? ResolveView(object? model)
    {
        if (model is null) return null;
        if (model is FrameworkElement view) return view;
        if (_views.TryGetValue(model, out var cached)) return cached;
        if (TryFindResource(new DataTemplateKey(model.GetType())) is not DataTemplate template) return null;
        var page = (FrameworkElement)template.LoadContent(); page.DataContext = model;
        _views.Add(model, page); return page;
    }
    private void ResetMotion() { BeginAnimation(OpacityProperty, null); _offset.BeginAnimation(TranslateTransform.YProperty, null); Opacity = 1; _offset.Y = 0; }
    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        var view = ResolveView(newContent);
        if (ReferenceEquals(PresentedView, view)) return;
        SetValue(PresentedViewPropertyKey, view);
        ResetMotion();
        if (!IsLoaded || MotionPolicy.Page == TimeSpan.Zero) return;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, MotionPolicy.Page) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        _offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, MotionPolicy.Page)
        { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }
}
