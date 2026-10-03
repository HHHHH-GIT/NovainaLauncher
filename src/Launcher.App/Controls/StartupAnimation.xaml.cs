using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Launcher.Core;

namespace Launcher.App.Controls;

/// <summary>A single, finite composition: assemble the star, then scatter its pixels and reveal the shell.</summary>
public partial class StartupAnimation : UserControl
{
    private FrameworkElement? _shell;
    private Geometry? _originalClip;
    private Transform? _originalTransform;
    private Point _originalOrigin;
    private readonly ScaleTransform _revealScale = new(0, 0);
    private readonly ScaleTransform _shellScale = new(.987, .987);
    private EllipseGeometry? _reveal;
    private Storyboard? _sequence;
    private TaskCompletionSource? _completion;
    private bool _finished;
    private bool _started;
    private readonly List<(Animatable Target, DependencyProperty Property, AnimationTimeline Animation)> _transforms = [];
    private readonly List<(Image Image, TranslateTransform Offset, ScaleTransform Scale, Vector Direction, double Spread)> _pieces = [];
    public bool IsActive => _shell is not null && !_finished;

    public StartupAnimation()
    {
        InitializeComponent();
        Unloaded += (_, _) => Finish();
    }

    // Called in the window constructor, before its very first frame. Never briefly show the shell.
    public void Prepare(FrameworkElement shell, bool enabled = true)
    {
        _shell = shell; _originalClip = shell.Clip; _originalTransform = shell.RenderTransform; _originalOrigin = shell.RenderTransformOrigin;
        if (!enabled || MotionPolicy.Effective == UiAnimationMode.Off) { Finish(); return; }
        shell.Opacity = 0; shell.IsHitTestVisible = false;
        shell.RenderTransformOrigin = new(.5, .5); shell.RenderTransform = _shellScale;
        _reveal = new EllipseGeometry { Transform = _revealScale };
        shell.Clip = _reveal;
        shell.SizeChanged += ShellSizeChanged;
        MotionPolicy.Changed += MotionChanged;
        Visibility = Visibility.Visible;
    }

    public async Task PlayAsync(CancellationToken cancellation = default)
    {
        if (!IsActive || _started) return;
        _started = true;
        if (cancellation.IsCancellationRequested || MotionPolicy.Effective == UiAnimationMode.Off) { Finish(); return; }
        // Layout/render has priority over background initialization, ensuring the covered first frame exists.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (!IsActive) return;
        UpdateReveal(); BuildPieces();
        var seconds = MotionPolicy.Effective == UiAnimationMode.Calm ? 2.8 : 1.8;
        var diagonal = Math.Sqrt(ActualWidth * ActualWidth + ActualHeight * ActualHeight);
        var haloScale = Math.Max(3, diagonal / 250);
        var sequence = _sequence = new Storyboard { Duration = TimeSpan.FromSeconds(seconds) };
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sequence.Completed += (_, _) => Finish();
        void Channel(DependencyObject target, DependencyProperty property, AnimationTimeline animation)
        {
            // Animate the live Freezable, rather than a storyboard's cloned transform reference.
            if (target is Animatable transform) _transforms.Add((transform, property, animation));
            else { Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, new PropertyPath(property)); sequence.Children.Add(animation); }
        }
        void Keys(DependencyObject target, DependencyProperty property, params (double At, double Value)[] points)
        {
            var frames = new DoubleAnimationUsingKeyFrames();
            foreach (var (at, value) in points)
                frames.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at * seconds)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            Channel(target, property, frames);
        }
        void Travel(DependencyObject target, DependencyProperty property, double from, double to, double start, double end, double power = 3)
        {
            // One zero-based clock for every channel: explicit holds keep transforms in sync with opacity.
            var motion = new DoubleAnimationUsingKeyFrames();
            motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start * seconds))));
            motion.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(end * seconds)), new PowerEase { Power = power, EasingMode = EasingMode.EaseOut }));
            Channel(target, property, motion);
        }
        Keys(Star, OpacityProperty, (0, 0), (.16, 1), (.21, 1), (.24, .38), (.275, 1), (.36, 1), (.395, 0));
        var starScale = (ScaleTransform)Star.RenderTransform;
        foreach (var axis in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            Keys(starScale, axis, (0, .86), (.18, 1.025), (.31, 1), (.37, 1.08), (.44, 1.32));
        Keys(BlueHalo, OpacityProperty, (0, 0), (.16, .18), (.34, .22), (.47, 1), (.72, .48), (1, 0));
        Keys(PurpleHalo, OpacityProperty, (0, 0), (.21, .12), (.34, .18), (.50, .95), (.78, .35), (1, 0));
        foreach (var halo in new[] { BlueHalo, PurpleHalo })
        {
            var scale = (ScaleTransform)halo.RenderTransform;
            foreach (var axis in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
                Travel(scale, axis, .3, haloScale * (halo == BlueHalo ? 1 : .92), .355, .95);
        }
        Keys(CoreFlash, OpacityProperty, (0, 0), (.34, 0), (.385, .9), (.50, 0));
        foreach (var axis in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            Travel((ScaleTransform)CoreFlash.RenderTransform, axis, .3, 2.7, .345, .57);
        Keys(Shockwave, OpacityProperty, (0, 0), (.36, 0), (.395, .6), (.62, .2), (.88, 0));
        foreach (var axis in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
            Travel((ScaleTransform)Shockwave.RenderTransform, axis, .7, Math.Max(7, diagonal / 140), .36, .9);
        foreach (var piece in _pieces)
        {
            var distance = diagonal * (.38 + piece.Spread * .24);
            Travel(piece.Offset, TranslateTransform.XProperty, 0, piece.Direction.X * distance, .38, .95);
            Travel(piece.Offset, TranslateTransform.YProperty, 0, piece.Direction.Y * distance, .38, .95);
            Keys(piece.Image, OpacityProperty, (0, 0), (.365, 0), (.38, 1), (.46, 1), (.78 + piece.Spread * .15, 0));
            foreach (var axis in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
                Travel(piece.Scale, axis, 1, .35 + piece.Spread * .45, .38, .93);
        }
        Keys(Curtain, OpacityProperty, (0, 1), (.36, 1), (.60, 0));
        Keys(_shell!, OpacityProperty, (0, 0), (.36, 0), (.75, 1));
        foreach (var axis in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            Travel(_revealScale, axis, 0, 1, .36, .96, 2.6);
            Travel(_shellScale, axis, .987, 1, .36, .98);
        }
        using var registration = cancellation.Register(() => Dispatcher.BeginInvoke(new Action(Finish)));
        sequence.Begin(this, true);
        foreach (var channel in _transforms) channel.Target.BeginAnimation(channel.Property, channel.Animation);
        await _completion.Task;
    }

    private void BuildPieces()
    {
        var source = new FormatConvertedBitmap((BitmapSource)Star.Source, PixelFormats.Bgra32, null, 0); source.Freeze();
        var stride = source.PixelWidth * 4; var pixels = new byte[stride * source.PixelHeight]; source.CopyPixels(pixels, stride, 0);
        const int cells = 8; var unit = 176d / cells;
        var random = new Random(731);
        for (var y = 0; y < cells; y++)
            for (var x = 0; x < cells; x++)
            {
                int left = x * source.PixelWidth / cells, top = y * source.PixelHeight / cells;
                var rect = new Int32Rect(left, top, source.PixelWidth / cells, source.PixelHeight / cells);
                var occupied = false;
                for (var py = top; py < top + rect.Height && !occupied; py++)
                    for (var px = left; px < left + rect.Width; px++)
                        if (pixels[py * stride + px * 4 + 3] > 24) { occupied = true; break; }
                if (!occupied) continue;
                var bitmap = new CroppedBitmap(source, rect); bitmap.Freeze();
                var offset = new TranslateTransform(); var scale = new ScaleTransform();
                var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(offset);
                var image = new Image { Source = bitmap, Width = unit, Height = unit, Opacity = 0, RenderTransformOrigin = new(.5, .5), RenderTransform = transforms };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(image, x * unit); Canvas.SetTop(image, y * unit); Fragments.Children.Add(image);
                var direction = new Vector(x - 3.5 + (random.NextDouble() - .5) * .7, y - 3.5 + (random.NextDouble() - .5) * .7); direction.Normalize();
                _pieces.Add((image, offset, scale, direction, random.NextDouble()));
            }
    }
    private void ShellSizeChanged(object sender, SizeChangedEventArgs e) => UpdateReveal();
    private void UpdateReveal()
    {
        if (_shell is null || _reveal is null) return;
        var center = new Point(_shell.ActualWidth / 2, _shell.ActualHeight / 2);
        _reveal.Center = center; _reveal.RadiusX = _reveal.RadiusY = Math.Sqrt(center.X * center.X + center.Y * center.Y) + 24;
        _revealScale.CenterX = center.X; _revealScale.CenterY = center.Y;
    }
    private void MotionChanged() => Finish();
    public void Finish()
    {
        if (_finished) return;
        _finished = true; _sequence?.Remove(this); _sequence = null;
        foreach (var channel in _transforms) channel.Target.BeginAnimation(channel.Property, null);
        _transforms.Clear();
        MotionPolicy.Changed -= MotionChanged;
        if (_shell is not null)
        {
            _shell.SizeChanged -= ShellSizeChanged;
            _shell.Clip = _originalClip; _shell.RenderTransform = _originalTransform; _shell.RenderTransformOrigin = _originalOrigin;
            _shell.Opacity = 1; _shell.IsHitTestVisible = true;
        }
        Visibility = Visibility.Collapsed; Fragments.Children.Clear(); _pieces.Clear();
        _completion?.TrySetResult();
    }
}
