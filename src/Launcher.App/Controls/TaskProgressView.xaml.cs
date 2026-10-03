using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Launcher.App.ViewModels;
using Launcher.Core;

namespace Launcher.App.Controls;

public partial class TaskProgressView : UserControl
{
    public static readonly DependencyProperty TaskProperty = DependencyProperty.Register(nameof(Task), typeof(DownloadTaskInfo), typeof(TaskProgressView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(TaskProgressView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(TaskProgressView));
    public DownloadTaskInfo? Task { get => (DownloadTaskInfo?)GetValue(TaskProperty); set => SetValue(TaskProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public ObservableCollection<DownloadConnectionRow> Connections { get; } = new();
    public TaskProgressView() => InitializeComponent();
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TaskProgressView)d).UpdateConnections();
    private void UpdateConnections()
    {
        if (!IsExpanded) return;
        var values = Task?.Progress?.Connections ?? [];
        foreach (var value in values)
        {
            var key = value.StableId ?? value.Id.ToString();
            var row = Connections.FirstOrDefault(c => c.Key == key);
            if (row is null) Connections.Add(new(key, value)); else row.Progress = value;
        }
        var active = values.Select(v => v.StableId ?? v.Id.ToString()).ToHashSet();
        for (var i = Connections.Count - 1; i >= 0; i--) if (!active.Contains(Connections[i].Key)) Connections.RemoveAt(i);
    }
}
