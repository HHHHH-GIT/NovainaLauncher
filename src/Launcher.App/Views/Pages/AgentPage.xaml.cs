using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launcher.App.Controls;
using Launcher.App.ViewModels;
using Launcher.Core;

namespace Launcher.App.Views.Pages;

public partial class AgentPage : UserControl
{
    private AgentViewModel? _vm;
    private int _introRevision;
    private bool _isComposing;
    private bool _followTail = true;
    private bool _scrollAnimating;
    private bool _timelineUpdatePending;
    private double _scrollTarget;
    private object? _lastTimelineItem;
    private ScrollViewer? TimelineScroll => TimelineList.Template?.FindName("PART_ScrollViewer", TimelineList) as ScrollViewer;
    private static readonly DependencyProperty TimelineOffsetProperty = DependencyProperty.Register(
        "TimelineOffset", typeof(double), typeof(AgentPage), new PropertyMetadata(0d, (d, e) =>
            ((AgentPage)d).TimelineScroll?.ScrollToVerticalOffset((double)e.NewValue)));
    public AgentPage()
    {
        InitializeComponent();
        TextCompositionManager.AddPreviewTextInputStartHandler(Composer, ComposerCompositionStarted);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Composer, ComposerCompositionStarted);
        Composer.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler(ComposerCompositionCompleted), true);
        Loaded += (_, _) => { MotionPolicy.Changed += MotionChanged; _vm = DataContext as AgentViewModel; if (_vm is not null) _vm.TimelineChanged += TimelineChanged; UpdatePlaceholder(); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= MotionChanged; if (_vm is not null) _vm.TimelineChanged -= TimelineChanged; _vm = null; FinishIntro(); StopTimelineScroll(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) PlayIntro(); else { FinishIntro(); StopTimelineScroll(); } };
    }
    private void TimelineChanged()
    {
        if (_timelineUpdatePending) return;
        _timelineUpdatePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _timelineUpdatePending = false;
            if (TimelineList.Items.Count == 0) { _followTail = true; _lastTimelineItem = null; return; }
            var last = TimelineList.Items[^1];
            var added = !ReferenceEquals(last, _lastTimelineItem);
            _lastTimelineItem = last;
            if (!_followTail || TimelineScroll is not { } scroll) return;
            StopTimelineScroll();
            TimelineList.UpdateLayout();
            if (added && last is AgentTimelineItem { IsQuestion: true })
            {
                // A question can be taller than the viewport. Reveal its beginning, not its end.
                TimelineList.ScrollIntoView(last); TimelineList.UpdateLayout();
                if (TimelineList.ItemContainerGenerator.ContainerFromItem(last) is FrameworkElement container)
                    scroll.ScrollToVerticalOffset(scroll.VerticalOffset + container.TranslatePoint(new Point(), scroll).Y);
                _followTail = false;
            }
            else scroll.ScrollToEnd();
        }));
    }
    private void TimelineScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, TimelineScroll) || _scrollAnimating) return;
        if (e.ExtentHeightChange != 0 && _followTail) TimelineChanged();
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && TimelineScroll is { } scroll)
            _followTail = scroll.ScrollableHeight - scroll.VerticalOffset < 24;
    }
    private void TimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TimelineScroll is not { } scroll || scroll.ScrollableHeight <= 0) return;
        // Handle on the outer list so option lists cannot consume the wheel or trap the card.
        e.Handled = true;
        var distance = SystemParameters.WheelScrollLines < 0 ? scroll.ViewportHeight : Math.Max(1, SystemParameters.WheelScrollLines) * 24d;
        var target = Math.Clamp((_scrollAnimating ? _scrollTarget : scroll.VerticalOffset) - e.Delta / 120d * distance, 0, scroll.ScrollableHeight);
        StopTimelineScroll(); _scrollTarget = target; _followTail = scroll.ScrollableHeight - target < 24;
        var duration = MotionPolicy.Effective switch { UiAnimationMode.Calm => 180, UiAnimationMode.Performance => 110, _ => 0 };
        if (duration == 0) { scroll.ScrollToVerticalOffset(target); return; }
        SetValue(TimelineOffsetProperty, scroll.VerticalOffset); _scrollAnimating = true;
        var animation = new DoubleAnimation(scroll.VerticalOffset, target, TimeSpan.FromMilliseconds(duration))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { SetValue(TimelineOffsetProperty, target); _scrollAnimating = false; };
        BeginAnimation(TimelineOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private void StopTimelineScroll()
    {
        var offset = TimelineScroll?.VerticalOffset ?? 0;
        BeginAnimation(TimelineOffsetProperty, null); SetValue(TimelineOffsetProperty, offset); _scrollAnimating = false;
    }
    private void TimelineMouseDown(object sender, MouseButtonEventArgs e) => StopTimelineScroll();
    private void MotionChanged() { FinishIntro(); StopTimelineScroll(); }
    private void ComposerCompositionStarted(object sender, TextCompositionEventArgs e) { _isComposing = true; UpdatePlaceholder(); }
    private void ComposerCompositionCompleted(object sender, TextCompositionEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { _isComposing = false; UpdatePlaceholder(); }));
    }
    private void ComposerTextChanged(object sender, TextChangedEventArgs e) => UpdatePlaceholder();
    private void ComposerLostFocus(object sender, KeyboardFocusChangedEventArgs e) { _isComposing = false; UpdatePlaceholder(); }
    private void UpdatePlaceholder()
    {
        if (ComposerPlaceholder is not null)
            ComposerPlaceholder.Visibility = !_isComposing && string.IsNullOrEmpty(Composer.Text) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ApiKeyChanged(object sender, RoutedEventArgs e) { if (DataContext is AgentViewModel vm) vm.PendingKey = ((PasswordBox)sender).Password; }
    private async void ConnectClick(object sender, RoutedEventArgs e)
    { if (DataContext is AgentViewModel vm) { await vm.ConnectCommand.ExecuteAsync(null); ApiKeyBox.Clear(); } }
    private void ComposerKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (_isComposing)
        {
            if (key == Key.Escape)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { _isComposing = false; UpdatePlaceholder(); }));
            return; // Enter first commits the IME candidate; it must not send the message.
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && DataContext is AgentViewModel vm)
        { e.Handled = true; if (vm.CanSend) vm.SendCommand.Execute(null); }
    }
    private void FinishIntro()
    {
        _introRevision++; Intro.BeginAnimation(OpacityProperty, null); Intro.Visibility = Visibility.Collapsed;
        AuroraBlue.BeginAnimation(OpacityProperty, null); AuroraPurple.BeginAnimation(OpacityProperty, null); IntroTitle.BeginAnimation(OpacityProperty, null);
        ((ScaleTransform)AuroraBlue.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, null); ((ScaleTransform)AuroraBlue.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ((TranslateTransform)AuroraPurple.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null); ((TranslateTransform)AuroraPurple.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
        ((TranslateTransform)IntroTitle.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
    }
    private async void PlayIntro()
    {
        FinishIntro(); var revision = _introRevision;
        var milliseconds = MotionPolicy.Effective switch { UiAnimationMode.Calm => 2000, UiAnimationMode.Performance => 1200, _ => 0 };
        if (milliseconds == 0) return;
        Intro.Visibility = Visibility.Visible; Intro.Opacity = 1;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void Animate(DependencyObject target, DependencyProperty property, double from, double to, double duration, double delay = 0)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(duration)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = ease };
            if (target is Animatable animatable) animatable.BeginAnimation(property, animation);
            else if (target is UIElement element) element.BeginAnimation(property, animation);
        }
        Animate(AuroraBlue, OpacityProperty, 0, .85, milliseconds * .45);
        Animate((ScaleTransform)AuroraBlue.RenderTransform, ScaleTransform.ScaleXProperty, .65, 1.25, milliseconds * .7);
        Animate((ScaleTransform)AuroraBlue.RenderTransform, ScaleTransform.ScaleYProperty, .65, 1.25, milliseconds * .7);
        Animate(AuroraPurple, OpacityProperty, 0, .9, milliseconds * .5, milliseconds * .15);
        Animate((TranslateTransform)AuroraPurple.RenderTransform, TranslateTransform.XProperty, 120, 0, milliseconds * .6);
        Animate((TranslateTransform)AuroraPurple.RenderTransform, TranslateTransform.YProperty, 70, 0, milliseconds * .6);
        Animate(IntroTitle, OpacityProperty, 0, 1, milliseconds * .3, milliseconds * .18);
        Animate((TranslateTransform)IntroTitle.RenderTransform, TranslateTransform.YProperty, 28, 0, milliseconds * .42, milliseconds * .18);
        try { await Task.Delay((int)(milliseconds * .73)); if (revision != _introRevision) return; Animate(Intro, OpacityProperty, 1, 0, milliseconds * .27); await Task.Delay((int)(milliseconds * .27)); if (revision == _introRevision) FinishIntro(); }
        catch (TaskCanceledException) { }
    }
}
