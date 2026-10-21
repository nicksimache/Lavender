using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace Lavender.App.Converters;

internal sealed class ExplorerIndentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string path || values[1] is not string root)
            return new Thickness(0);

        string relative = Path.GetRelativePath(root, path);
        int depth = relative == "." ? 0 : relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries).Length;
        return new Thickness(depth * 12, 0, 0, 0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
