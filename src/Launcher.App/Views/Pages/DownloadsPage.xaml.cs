using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launcher.App.Controls;
using Launcher.App.ViewModels;
namespace Launcher.App.Views.Pages;
public partial class DownloadsPage : UserControl
{
    private DownloadsViewModel? _model;
    private string? _screen;
    private bool _scheduled;
    public DownloadsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Connect();
        Loaded += (_, _) => { Connect(); MotionPolicy.Changed += Reset; };
        Unloaded += (_, _) => { if (_model is not null) _model.PropertyChanged -= Changed; _model = null; MotionPolicy.Changed -= Reset; Reset(); };
    }
    private void Connect()
    {
        if (_model is not null) _model.PropertyChanged -= Changed;
        _model = DataContext as DownloadsViewModel;
        if (_model is not null) { _model.PropertyChanged += Changed; _screen = _model.Screen; }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DownloadsViewModel.Screen) || _screen == _model?.Screen) return;
        _screen = _model?.Screen;
        if (_scheduled) return;
        _scheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _scheduled = false; Reset();
            if (!IsLoaded || _model?.IsFlow != true || MotionPolicy.Page == TimeSpan.Zero) return;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var offset = (TranslateTransform)StepSurface.RenderTransform;
            offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_model.Direction * 64, 0, MotionPolicy.Page) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            StepSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, MotionPolicy.Page) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            HeroTitle.RenderTransform = new TranslateTransform();
            ((TranslateTransform)HeroTitle.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, MotionPolicy.Page) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            HeroTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, MotionPolicy.Page) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        }));
    }
    private void Reset()
    {
        StepSurface.BeginAnimation(OpacityProperty, null); StepSurface.Opacity = 1;
        if (StepSurface.RenderTransform is TranslateTransform t) { t.BeginAnimation(TranslateTransform.XProperty, null); t.X = 0; }
        else StepSurface.RenderTransform = new TranslateTransform();
        HeroTitle.BeginAnimation(OpacityProperty, null); HeroTitle.Opacity = 1;
        if (HeroTitle.RenderTransform is TranslateTransform title) { title.BeginAnimation(TranslateTransform.YProperty, null); title.Y = 0; }
    }
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DownloadsViewModel vm) { vm.SearchCommand.Execute(null); e.Handled = true; }
    }
}
