using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Lavender.App.Converters;

public sealed class ProjectNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string path && !string.IsNullOrWhiteSpace(path)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : "General chats";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
