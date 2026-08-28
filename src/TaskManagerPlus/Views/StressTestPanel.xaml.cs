using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Views;

/// <summary>
/// #697: starts/stops the GPU-load render-loop Storyboard (see StressTestPanel.xaml's
/// UserControl.Resources remarks for why this has to be driven from code-behind -
/// Storyboard.TargetName needs a real NameScope to resolve the animated shapes against, which a
/// plain Style.Triggers Storyboard doesn't have) in response to
/// StressTestViewModel.IsGpuRenderActive changing, rather than a XAML DataTrigger.
/// </summary>
public partial class StressTestPanel : UserControl
{
    private Storyboard? _gpuLoadStoryboard;

    public StressTestPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is StressTestViewModel oldVm) oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is StressTestViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            ApplyGpuRenderState(newVm.IsGpuRenderActive);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StressTestViewModel.IsGpuRenderActive) || sender is not StressTestViewModel vm) return;
        Dispatcher.Invoke(() => ApplyGpuRenderState(vm.IsGpuRenderActive));
    }

    private void ApplyGpuRenderState(bool active)
    {
        _gpuLoadStoryboard ??= (Storyboard)FindResource("GpuLoadStoryboard");
        if (active) _gpuLoadStoryboard.Begin(this, true);
        else _gpuLoadStoryboard.Stop(this);
    }
}
