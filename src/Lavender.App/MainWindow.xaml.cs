using Lavender.App.Rendering;
using Lavender.Application.Agent;
using Lavender.Infrastructure.AI;
using Lavender.Infrastructure.Backend;
using Lavender.Infrastructure.FileSystem;
using Lavender.Infrastructure.Indexing;
using Lavender.Infrastructure.Indexing.Symbol;
using Lavender.Infrastructure.Mcp;
using Lavender.Infrastructure.Retrieval;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Lavender.App
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool isHighlighting = false;
        private string? currSelectedFile;
        private string? _selectedProjectPath;
        private string? _selectedSolutionPath;

        // nullable services since they require project dir as constructor fields
        private ProjectScanner? _projectScanner;
        private ProjectSearchService? _projectSearchService;


        private readonly AgentRunner _agentRunner;
        private readonly LavenderMcpClient _mcpClient;
        private readonly ProjectIndexer _projectIndexer;
        private Guid _activeConversationId;
        private readonly List<string> contextFiles = new();

        #region Constructor
        /// <summary>
        /// Default constructor
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            AgentSettings settings = AgentSettings.Load();
            settings.Validate();
            _mcpClient = new LavenderMcpClient(FindRepositoryRoot());
            JsonConversationStore conversations = new(settings.HistoryDirectory);
            OpenAIService model = new(settings.Model);
            _agentRunner = new AgentRunner(
                settings,
                model,
                _mcpClient,
                conversations);

            _projectIndexer = new ProjectIndexer();

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            await FastApiService.Instance.StartServerAsync();
            await _mcpClient.ConnectAsync();
            Conversation conversation = await _agentRunner.CreateConversationAsync();
            _activeConversationId = conversation.Id;
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

        protected override void OnClosed(EventArgs e)
        {
            _projectIndexer.Dispose();
            _mcpClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            FastApiService.Instance.StopServer();
            base.OnClosed(e);
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

            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            AddMessageBubble(input, true);
            UserInputBox.Text = "";

            try
            {
                AgentRunResult result = await _agentRunner.RunAsync(
                    _activeConversationId,
                    input,
                    BuildProjectContext());
                AddMessageBubble(result.FinalAnswer, false);
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
        private async void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select a Unity Project"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string selectedPath = dialog.FolderName;
            string solutionOrProjectPath;
            try
            {
                solutionOrProjectPath = FindSolution(selectedPath);
            }
            catch (InvalidOperationException err)
            {
                MessageBox.Show(
                    err.Message,
                    "Unable to open project",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            FolderView.Items.Clear();

            var rootItem = new TreeViewItem
            {
                Header = Path.GetFileName(selectedPath),
                Tag = selectedPath
            };

            rootItem.Items.Add(null);
            rootItem.Expanded += Folder_Expanded;
            FolderView.Items.Add(rootItem);
            rootItem.IsExpanded = true;

            _projectScanner = new ProjectScanner(selectedPath);
            _projectSearchService = new ProjectSearchService(_projectScanner);
            _selectedProjectPath = selectedPath;
            _selectedSolutionPath = solutionOrProjectPath;

            try
            {
                // We're embedding the project here for now.
                await _projectIndexer.IndexProjectAsync(
                    selectedPath,
                    solutionOrProjectPath);
                await _mcpClient.IndexProjectAsync(
                    selectedPath,
                    solutionOrProjectPath);
            }
            catch (Exception err)
            {
                MessageBox.Show(
                    $"Lavender could not index the selected project.{Environment.NewLine}{Environment.NewLine}{err.Message}",
                    "Project indexing failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string FindSolution(string directory)
        {
            string? solutionPath = Directory
                .EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (solutionPath is not null)
            {
                return solutionPath;
            }

            string[] projectPaths = Directory
                .EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                .ToArray();

            if (projectPaths.Length == 1)
            {
                return projectPaths[0];
            }

            throw new InvalidOperationException(
                "The selected folder does not contain a solution or a single project file.");
        }

        private string BuildProjectContext()
        {
            if (_selectedProjectPath is null || _selectedSolutionPath is null)
            {
                return "No project is selected. Ask the user to open a project before code analysis.";
            }

            string selectedFiles = contextFiles.Count == 0
                ? "None"
                : string.Join(Environment.NewLine, contextFiles);

            return $"""
                Project root: {_selectedProjectPath}
                Solution or project: {_selectedSolutionPath}
                User-selected context files:
                {selectedFiles}
                """;
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Lavender.sln")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }

            if (File.Exists(Path.Combine(Environment.CurrentDirectory, "Lavender.sln")))
            {
                return Environment.CurrentDirectory;
            }

            throw new DirectoryNotFoundException(
                "Could not locate the Lavender repository root.");
        }

        /// <summary>
        /// Event handler for when a folder is expanded
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            var item = (TreeViewItem)sender;

            if (item.Items.Count != 1 || item.Items[0] != null)
            {
                return;
            }

            item.Items.Clear();

            var fullPath = (string)item.Tag;

            #region Get Folders

            var directories = new List<string>();

            try
            {
                var dirs = Directory.GetDirectories(fullPath);

                if (dirs.Length > 0)
                {
                    foreach (var d in dirs)
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
                    foreach (var f in fs)
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
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var normalizedPath = path.Replace('/', '\\');

            var lastIndex = normalizedPath.LastIndexOf('\\');
            if (lastIndex <= 0)
            {
                return path;
            }

            return normalizedPath.Substring(lastIndex + 1);
        }

        private void FolderView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is not TreeViewItem item)
            {
                return;
            }

            if (item.Tag is not string path)
            {
                return;
            }

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
