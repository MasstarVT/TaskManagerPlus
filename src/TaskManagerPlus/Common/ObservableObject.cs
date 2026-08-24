using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TaskManagerPlus.Common;

/// <summary>
/// Minimal INotifyPropertyChanged base class. Avoids pulling in a full MVVM
/// framework for a project this size.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Sets the backing field and raises PropertyChanged only if the value actually changed.
    /// Returns true when the value changed.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
