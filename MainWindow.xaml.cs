using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Lavender.Services;
using System.IO;
using Lavender.Search;

namespace Lavender
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool isHighlighting = false;
        private Point dragStartPoint;
        private string? currSelectedFile;

        // nullable services since they require project dir as constructor fields
        private ProjectScanner? _projectScanner;
        private ProjectSearchService? _projectSearchService;


        private readonly OpenAIService _openAIService;
        private readonly PromptBuilder _promptBuilder;
        private readonly ProjectSearchService projectSearchService;
        private readonly List<string> contextFiles = new();

        #region Constructor
        /// <summary>
        /// Default constructor
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            _openAIService = new OpenAIService();

            var fileParser = new FileParser();
            _promptBuilder = new PromptBuilder(fileParser);
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
            string input = UserInputBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(input)) return;

            AddMessageBubble(input, true);
            UserInputBox.Text = "";

            if (_projectSearchService == null)
                return;

            ProjectSearchService projectSearchService = _projectSearchService;
            List<SearchResult> SearchResult = projectSearchService.Search(input);
            List<string> keywordSearchFiles = new List<string>();

            foreach(var searchResult in SearchResult)
            {
                keywordSearchFiles.Add(searchResult.FilePath);
            }

            string userPrompt = _promptBuilder.PromptOnFileContext(contextFiles, keywordSearchFiles, input);

            foreach (var file in keywordSearchFiles)
            {
                AddMessageBubble(file, false);
            }

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
            var dialog = new OpenFolderDialog
            {
                Title = "Select a Unity Project"
            };

            if (dialog.ShowDialog() != true) { return; }

            string selectedPath = dialog.FolderName;
            FolderView.Items.Clear();

            var rootItem = new TreeViewItem
            {
                Header = Path.GetFileName(selectedPath),
                Tag = selectedPath
            };

            rootItem.Items.Add(null);
            rootItem.Expanded += Folder_Expanded;

            FolderView.Items.Add(rootItem);

            _projectScanner = new ProjectScanner(selectedPath);
            _projectSearchService = new ProjectSearchService(_projectScanner);

            rootItem.IsExpanded = true;
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

                if(dirs.Length > 0)
                {
                    foreach(var d in dirs)
                    {
                        if (!ProjectScanner.ShouldIgnoreFolder(d))
                        {
                            directories.Add(d);
                        }
                    }
                }
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

                if (fs.Length > 0) 
                {
                    foreach(var f in fs)
                    {
                        if (!ProjectScanner.ShouldIgnoreFile(f))
                        {
                            files.Add(f);
                        }
                    }
                }
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

        private void FolderView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeViewItem item) return;
            if (item.Tag is not string path) return;

            if (File.Exists(path) &&
                Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                currSelectedFile = path;

                PreviewFileNameText.Text = Path.GetFileName(currSelectedFile);

                string code = File.ReadAllText(currSelectedFile);
                ShowCodeInPreview(code);
            }
        }

        private void FilePreviewBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (isHighlighting)
                return;

            HighlightCurrentDocument();
        }

        private void ShowCodeInPreview(string code)
        {
            isHighlighting = true;

            var spans = SyntaxHighlighter.HighlightCSharpCode(code);
            RichTextBoxRenderer.Render(FilePreviewBox, spans);

            isHighlighting = false;
        }

        private void HighlightCurrentDocument()
        {
            if (isHighlighting)
                return;

            string code = new TextRange(
                FilePreviewBox.Document.ContentStart,
                FilePreviewBox.Document.ContentEnd
            ).Text;

            ShowCodeInPreview(code);
        }

        private void FolderView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            if (FolderView.SelectedItem is not TreeViewItem item)
                return;

            if (item.Tag is not string path)
                return;

            if (!File.Exists(path))
                return;

            if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                return;

            DragDrop.DoDragDrop(FolderView, path, DragDropEffects.Copy);
        }

        private void ChatPanel_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;

            e.Handled = true;
        }

        private void ChatPanel_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.StringFormat))
                return;

            string? path = e.Data.GetData(DataFormats.StringFormat) as string;

            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!File.Exists(path))
                return;

            if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                return;

            if (!contextFiles.Contains(path))
                contextFiles.Add(path);

            SelectedContextText.Text = $"Context: {contextFiles.Count} file(s)";
        }


        #endregion

    }
}