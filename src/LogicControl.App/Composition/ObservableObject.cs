using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LogicControl.App.Composition;

/// <summary>
/// The whole of the MVVM plumbing this app needs: raise PropertyChanged, and only when a value
/// actually changed.
///
/// <para>NetControl uses CommunityToolkit.Mvvm's source generator for this. This app does not,
/// on purpose: its view models are bound once per opened file rather than ticking live, so the
/// generator would save little, and keeping them free of it means they compile and test on a
/// machine with no NuGet access - which is where the first version of this project was built.</para>
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
