using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Lavender.App.Converters
{
    [ValueConversion(typeof(string), typeof(BitmapImage))]
    internal class HeaderToImageConverter : IValueConverter
    {
        public static HeaderToImageConverter Instance = new HeaderToImageConverter();

        private readonly string csIcon = "/src/Lavender.App/Assets/Images/C_Sharp_Logo_2023.png";
        private readonly string fileIcon = "";

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string path)
                return null;

            string icon = "";

            if (!new FileInfo(path).Attributes.HasFlag(FileAttributes.Directory))
            {
                icon = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    ? csIcon
                    : fileIcon;
            }

            if (string.IsNullOrWhiteSpace(icon))
                return null;

            return new BitmapImage(new Uri(icon, UriKind.Relative));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
