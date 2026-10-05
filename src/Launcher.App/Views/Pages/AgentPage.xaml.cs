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
    private readonly DispatcherTimer _scrollTimer;
    private double _scrollDistance, _scrollConsumed;
    private long _scrollStarted;
    private int _scrollDuration;
    private object? _lastTimelineItem;
    private bool _contextPinned;
    private Window? _hostWindow;
    private ScrollViewer? TimelineScroll => TimelineList.Template?.FindName("PART_ScrollViewer", TimelineList) as ScrollViewer;
    public AgentPage()
    {
        InitializeComponent();
        _scrollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += ScrollFrame;
        TextCompositionManager.AddPreviewTextInputStartHandler(Composer, ComposerCompositionStarted);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Composer, ComposerCompositionStarted);
        Composer.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler(ComposerCompositionCompleted), true);
        Composer.SelectionChanged += (_, _) => RefreshSuggestions();
        Loaded += (_, _) =>
        {
            MotionPolicy.Changed += MotionChanged; _vm = DataContext as AgentViewModel;
            if (_vm is not null) { _vm.TimelineChanged += TimelineChanged; _vm.FocusComposerRequested += FocusComposer; _vm.PropertyChanged += ViewModelChanged; }
            _hostWindow = Window.GetWindow(this); if (_hostWindow is not null) _hostWindow.StateChanged += HostStateChanged;
            UpdatePlaceholder();
        };
        Unloaded += (_, _) =>
        {
            MotionPolicy.Changed -= MotionChanged; CloseFloatingPanels();
            if (_vm is not null) { _vm.TimelineChanged -= TimelineChanged; _vm.FocusComposerRequested -= FocusComposer; _vm.PropertyChanged -= ViewModelChanged; }
            if (_hostWindow is not null) _hostWindow.StateChanged -= HostStateChanged; _hostWindow = null; _vm = null; FinishIntro(); StopTimelineScroll();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) PlayIntro(); else { CloseFloatingPanels(); FinishIntro(); StopTimelineScroll(); } };
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
            if (_scrollAnimating) return; // Never interrupt a user's wheel gesture for a streamed delta.
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
        var movement = -e.Delta / 120d * distance;
        var target = Math.Clamp(scroll.VerticalOffset + (_scrollDistance - _scrollConsumed) + movement, 0, scroll.ScrollableHeight);
        _followTail = movement > 0 && scroll.ScrollableHeight - target < 24;
        var duration = MotionPolicy.Effective switch { UiAnimationMode.Calm => 180, UiAnimationMode.Performance => 110, _ => 0 };
        if (duration == 0) { StopTimelineScroll(); scroll.ScrollToVerticalOffset(target); return; }
        _scrollDistance = target - scroll.VerticalOffset; _scrollConsumed = 0; _scrollStarted = Environment.TickCount64; _scrollDuration = duration;
        _scrollAnimating = true; _scrollTimer.Start();
    }
    private void ScrollFrame(object? sender, EventArgs e)
    {
        if (TimelineScroll is not { } scroll) { StopTimelineScroll(); return; }
        var progress = Math.Clamp((Environment.TickCount64 - _scrollStarted) / (double)_scrollDuration, 0, 1);
        var consumed = _scrollDistance * (1 - Math.Pow(1 - progress, 3));
        var step = consumed - _scrollConsumed; _scrollConsumed = consumed;
        // Relative movement keeps the virtualizer's current anchor when measured row
        // heights change. Animating an absolute estimated offset repeatedly reset it.
        scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + step, 0, scroll.ScrollableHeight));
        if (progress >= 1) StopTimelineScroll();
    }
    private void StopTimelineScroll()
    {
        _scrollTimer.Stop(); _scrollDistance = _scrollConsumed = 0; _scrollAnimating = false;
    }
    private void TimelineMouseDown(object sender, MouseButtonEventArgs e) { StopTimelineScroll(); _followTail = false; }
    private void MotionChanged() { FinishIntro(); StopTimelineScroll(); }
    private void ComposerCompositionStarted(object sender, TextCompositionEventArgs e) { _isComposing = true; if (_vm is not null) _vm.ShowSuggestions = false; UpdatePlaceholder(); }
    private void ComposerCompositionCompleted(object sender, TextCompositionEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { _isComposing = false; UpdatePlaceholder(); RefreshSuggestions(); }));
    }
    private void ComposerTextChanged(object sender, TextChangedEventArgs e) { UpdatePlaceholder(); RefreshSuggestions(); }
    private void ComposerLostFocus(object sender, KeyboardFocusChangedEventArgs e) { _isComposing = false; UpdatePlaceholder(); }
    private void UpdatePlaceholder()
    {
        if (ComposerPlaceholder is not null)
            ComposerPlaceholder.Visibility = !_isComposing && string.IsNullOrEmpty(Composer.Text) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ApiKeyChanged(object sender, RoutedEventArgs e) { if (DataContext is AgentViewModel vm) vm.PendingKey = ((PasswordBox)sender).Password; }
    private async void ConnectClick(object sender, RoutedEventArgs e)
    { if (DataContext is AgentViewModel vm) { await vm.ConnectCommand.ExecuteAsync(null); ApiKeyBox.Clear(); } }
    private async void ComposerKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (_isComposing)
        {
            if (key == Key.Escape)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { _isComposing = false; UpdatePlaceholder(); }));
            return; // Enter first commits the IME candidate; it must not send the message.
        }
        if (DataContext is not AgentViewModel vm || Keyboard.Modifiers != ModifierKeys.None) return;
        if (vm.ShowSuggestions)
        {
            if (e.Key is Key.Up or Key.Down)
            {
                e.Handled = true; var index = vm.SelectedSuggestion is null ? 0 : vm.Suggestions.IndexOf(vm.SelectedSuggestion);
                vm.SelectedSuggestion = vm.Suggestions[Math.Clamp(index + (e.Key == Key.Up ? -1 : 1), 0, vm.Suggestions.Count - 1)]; SuggestionsList.ScrollIntoView(vm.SelectedSuggestion); return;
            }
            if (e.Key is Key.Enter or Key.Tab && vm.SelectedSuggestion is { } selected) { e.Handled = true; await vm.AcceptSuggestionAsync(selected); return; }
            if (e.Key == Key.Escape) { e.Handled = true; vm.ShowSuggestions = false; return; }
        }
        if (e.Key == Key.Enter) { e.Handled = true; if (await vm.TryExecuteLocalInputAsync()) return; if (vm.CanSend) await vm.SendCommand.ExecuteAsync(null); }
    }
    private void RefreshSuggestions()
    { if (!_isComposing && Composer is { IsKeyboardFocusWithin: true } && DataContext is AgentViewModel vm) vm.UpdateSuggestions(Composer.CaretIndex); }
    private void FocusComposer(int caret) { Composer.Focus(); Composer.CaretIndex = Math.Clamp(caret, 0, Composer.Text.Length); if (_vm is not null) _vm.ShowSuggestions = false; }
    private async void SuggestionClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(SuggestionsList, source) is ListBoxItem { DataContext: AgentComposerSuggestion suggestion } && DataContext is AgentViewModel vm)
        { e.Handled = true; await vm.AcceptSuggestionAsync(suggestion); Composer.Focus(); }
    }
    private void SurfaceMouseDown(object sender, MouseButtonEventArgs e)
    { if (e.OriginalSource is DependencyObject source && !IsInside(source, ComposerSurface) && _vm is not null) _vm.ShowSuggestions = false; }
    private static bool IsInside(DependencyObject source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private void ContextRingEntered(object sender, MouseEventArgs e) => ContextPopup.IsOpen = true;
    private void PermissionClicked(object sender, RoutedEventArgs e) => PermissionPopup.IsOpen = !PermissionPopup.IsOpen;
    private void ContextRingLeft(object sender, MouseEventArgs e) { if (!_contextPinned) ContextPopup.IsOpen = false; }
    private void ContextRingClicked(object sender, RoutedEventArgs e) { _contextPinned = true; ContextPopup.IsOpen = true; }
    private void ContextPopupClosed(object sender, RoutedEventArgs e) { _contextPinned = false; ContextPopup.IsOpen = false; }
    private void CloseFloatingPanels() { _contextPinned = false; if (ContextPopup is not null) ContextPopup.IsOpen = false; if (PermissionPopup is not null) PermissionPopup.IsOpen = false; if (_vm is not null) _vm.ShowSuggestions = false; }
    private void HostStateChanged(object? sender, EventArgs e) { if (_hostWindow?.WindowState == WindowState.Minimized) CloseFloatingPanels(); }
    private void ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentViewModel.IsFullAccess) or nameof(AgentViewModel.ShowFullAccessWarning)) PermissionPopup.IsOpen = false;
        if (e.PropertyName == nameof(AgentViewModel.ShowWelcome) && _vm?.ShowWelcome == true) CloseFloatingPanels();
        if (e.PropertyName == nameof(AgentViewModel.ShowGoalEditor) && _vm?.ShowGoalEditor == true) { CloseFloatingPanels(); Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => GoalInput.Focus())); }
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
