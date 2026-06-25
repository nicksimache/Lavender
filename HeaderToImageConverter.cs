using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Lavender
{
    [ValueConversion(typeof(string), typeof(BitmapImage))]
    internal class HeaderToImageConverter : IValueConverter
    {
        public static HeaderToImageConverter Instance = new HeaderToImageConverter();

        private string cs_icon = "Images/C_Sharp_Logo_2023.png";
        private string file_icon = "";

        /// <summary>
        /// Converts a full path to a specific image type: drive, folder, file
        /// </summary>
        /// <param name="value"></param>
        /// <param name="targetType"></param>
        /// <param name="parameter"></param>
        /// <param name="culture"></param>
        /// <returns></returns>
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = (string)value;

            if(path == null) { return null; }

            var name = MainWindow.GetFileFolderName(path);

            var icon = "";

            if(new FileInfo(path).Attributes.HasFlag(FileAttributes.Directory))
            {
                // Remove the image, right now its blank
            }
            else
            {
                if (path.EndsWith(".cs"))
                {
                    icon = cs_icon;
                }
                else
                {
                    icon = file_icon;
                }
            }

            return new BitmapImage(
                new Uri($"/{icon}", UriKind.Relative)
            );
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
