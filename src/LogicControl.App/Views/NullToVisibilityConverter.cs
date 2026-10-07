using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LogicControl.App.Views;

/// <summary>
/// Shows an element only when the bound value is present - or, with <see cref="Invert"/>, only
/// when it is absent. Used for the error banner, which is absent far more often than not, and for
/// the Develop tab's "nothing selected" note, which is the inverse.
/// </summary>
[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Visible when the value is null or blank instead.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is null || (value is string text && string.IsNullOrWhiteSpace(text))) != Invert
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("One way only.");
}
