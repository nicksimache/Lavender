using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using Lavender.Services;
using System.IO;

namespace Lavender
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly OpenAIService _openAIService;

        #region Constructor
        /// <summary>
        /// Default constructor
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            _openAIService = new OpenAIService();
        }

        #endregion

        #region On Loaded
        /// <summary>
        /// Event handler for when the window first loads
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {

        }

        #endregion

        #region Chat

        /// <summary>
        /// Event handler for when the user sends a request to OpenAI API
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            string userPrompt = UserInputBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(userPrompt)) return;

            AddMessageBubble(userPrompt, true);
            UserInputBox.Text = "";

            try
            {
                string aiResponse = await _openAIService.AskAsync(userPrompt);
                AddMessageBubble(aiResponse, false);
            }
            catch (Exception ex)
            {
                AddMessageBubble($"Error: {ex.Message}", false);
            }
        }

        private void AddMessageBubble(string message, bool isUser)
        {
            Border bubble = new Border
            {
                Background = isUser
                ? new SolidColorBrush(Color.FromRgb(124, 58, 237))
                : new SolidColorBrush(Color.FromRgb(31, 41, 55)),

                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                Margin = isUser
                ? new Thickness(120, 0, 0, 16)
                : new Thickness(0, 0, 120, 16),
            };

            TextBlock text = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap
            };

            bubble.Child = text;

            ChatMessagesPanel.Children.Add(bubble);
            ChatScrollViewer.ScrollToEnd();
        }

        #endregion

        #region File System

        /// <summary>
        /// Opens file directory for user to select a valid unity project
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            foreach (var drive in Directory.GetLogicalDrives())
            {
                var item = new TreeViewItem();

                item.Header = drive;
                item.Tag = drive;

                item.Items.Add(null);

                item.Expanded += Folder_Expanded;

                FolderView.Items.Add(item);
            }
        }

        /// <summary>
        /// Event handler for when a folder is expanded
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            var item = (TreeViewItem)sender;

            if (item.Items.Count != 1 || item.Items[0] != null) return;

            item.Items.Clear();

            var fullPath = (string)item.Tag;

            #region Get Folders

            var directories = new List<string>();

            try
            {
                var dirs = Directory.GetDirectories(fullPath);

                if(dirs.Length > 0) { directories.AddRange(dirs); }
            }
            catch { }

            directories.ForEach(directoryPath =>
            {
                var subitem = new TreeViewItem()
                {
                    Header = GetFileFolderName(directoryPath),
                    Tag = directoryPath
                };

                subitem.Items.Add(null);

                subitem.Expanded += Folder_Expanded;

                item.Items.Add(subitem);
            });

            #endregion

            #region Get Files

            var files = new List<string>();

            try
            {
                var fs = Directory.GetFiles(fullPath);

                if (fs.Length > 0) { files.AddRange(fs); }
            }
            catch { }

            files.ForEach(filePath =>
            {
                var subitem = new TreeViewItem()
                {
                    Header = GetFileFolderName(filePath),
                    Tag = filePath
                };

                item.Items.Add(subitem);
            });

            #endregion



        }

        /// <summary>
        /// Find the file or folder name from a full path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string GetFileFolderName(string path)
        {
            if (string.IsNullOrEmpty(path)) { return string.Empty; }

            var normalizedPath = path.Replace('/', '\\');

            var lastIndex = normalizedPath.LastIndexOf('\\');
            if(lastIndex <= 0) { return path; }

            return normalizedPath.Substring(lastIndex+1);
        }

        #endregion
        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}