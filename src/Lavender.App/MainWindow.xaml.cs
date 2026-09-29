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

        private string? currSelectedFile;
        private readonly System.Collections.ObjectModel.ObservableCollection<OpenFileTab> _openFiles = new();
        private readonly LinkedList<string> _recentFiles = new(); // Most recent path first; no duplicates.
        private OpenFileTab? _activeFile;
        private string? _selectedProjectPath;
        private string? _selectedSolutionPath;
        private static readonly HashSet<string> ExplorerFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".xaml",
            ".py",
            ".csproj",
            ".sln",
            ".json",
            ".md",
            ".txt"
        };

        private static readonly HashSet<string> ExplorerFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".gitignore",
            "requirements.txt"
        };

        // nullable services since they require project dir as constructor fields
        private ProjectScanner? _projectScanner;
        private ProjectSearchService? _projectSearchService;


        private readonly AgentRunner _agentRunner;
        private readonly JsonConversationStore _conversations;
        private bool _busy;
        private bool _refreshingHistory;
        private bool _ready;
        private bool _indexing;
        private (string ProjectPath, string SolutionPath)? _pendingProject;
        private CancellationTokenSource? _activeRun;
        private readonly CancellationTokenSource _windowLifetime = new();
        private readonly LastProjectStore _lastProjectStore = new();
        private readonly LavenderMcpClient _mcpClient;
        private Guid _activeConversationId;
        private readonly List<string> contextFiles = new();

        #region Constructor
        /// <summary>
        /// Default constructor
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            ConfigureCodeEditor();
            OpenFileTabs.ItemsSource = _openFiles;

            AgentSettings settings = AgentSettings.Load();
            settings.Validate();
            _mcpClient = new LavenderMcpClient(FindRepositoryRoot());
            _conversations = new(settings.HistoryDirectory, settings.PersistHistory);
            OpenAIService model = new(settings.Model);
            _agentRunner = new AgentRunner(
                settings,
                model,
                _mcpClient,
                _conversations);
            _agentRunner.Progress += status => Dispatcher.InvokeAsync(() =>
            {
                AgentStatusText.Text = status;
                AppendAgentActivity(status);
            });
            _agentRunner.ToolCompleted += record => Dispatcher.InvokeAsync(() => AppendToolActivity(record));

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await InitializeAsync();
            }
            catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Lavender startup failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private async Task InitializeAsync()
        {
            // Paint the loading screen before restoring the explorer and starting services.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            LastProject? previous = await _lastProjectStore.LoadAsync(_windowLifetime.Token);
            if (previous is not null)
                await DisplayProjectAsync(previous.ProjectPath, previous.SolutionPath);
            else
                await RestoreProjectChatAsync();
            AgentStatusText.Text = "Starting backend…";
            StartupStatusText.Text = "Starting backend server…";
            await FastApiService.Instance.StartFreshServerAsync(_windowLifetime.Token);
            AgentStatusText.Text = "Connecting tools…";
            StartupStatusText.Text = "Connecting MCP tools…";
            await _mcpClient.ConnectAsync(_windowLifetime.Token);
            _windowLifetime.Token.ThrowIfCancellationRequested();
            _ready = true;
            AgentStatusText.Text = "Ready";
            WorkspaceContent.Visibility = Visibility.Visible;
            StartupOverlay.Visibility = Visibility.Collapsed;
            if (_pendingProject is not null)
                await OpenPendingProjectAsync();
            else if (previous is not null)
                await OpenProjectAsync(previous.ProjectPath, previous.SolutionPath, alreadyDisplayed: true);
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
            _windowLifetime.Cancel();
            _activeRun?.Cancel();
            try
            {
                FastApiService.Instance.StopServer();
            }
            finally
            {
                try { _mcpClient.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                finally { base.OnClosed(e); }
            }
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
            if (_activeRun is not null) { _activeRun.Cancel(); return; }
            await SendCurrentQueryAsync();
        }

        private async void UserInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                return;
            }

            e.Handled = true;
            await SendCurrentQueryAsync();
        }

        private async Task SendCurrentQueryAsync()
        {
            if (!_ready || _busy) return;
            if (_activeConversationId == Guid.Empty)
            {
                AddMessageBubble("Choose New chat before sending a message.", false);
                return;
            }
            string input = UserInputBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            AddMessageBubble(input, true);
            UserInputBox.Text = "";

            try
            {
                _activeRun = new CancellationTokenSource();
                SetBusy(true);
                AgentRunResult result = await _agentRunner.RunAsync(
                    _activeConversationId,
                    input,
                    BuildProjectContext(), _activeRun.Token);

                foreach (string diagnostic in result.ToolDiagnostics ?? [])
                {
                    // Lets ignore this for now
                    // AddMessageBubble($"Tool error: {diagnostic}", false);
                }

                AddMessageBubble(result.FinalAnswer, false);
                if (result.ProjectFilesChanged && _selectedProjectPath is not null)
                {
                    FolderView.Items.Clear();
                    TreeViewItem? root = BuildDisplayableExplorerDirectory(_selectedProjectPath, includeIfEmpty: true);
                    if (root is not null) { FolderView.Items.Add(root); root.IsExpanded = true; }
                    foreach (var tab in _openFiles) tab.ReloadIfClean();
                    if (contextFiles.RemoveAll(path => !File.Exists(path)) > 0) await SaveContextFilesAsync();
                }
            }
            catch (OperationCanceledException)
            {
                AddMessageBubble("Response cancelled.", false);
            }
            catch (Exception ex)
            {
                AddMessageBubble($"Error: {ex.Message}", false);
            }
            finally
            {
                _activeRun?.Dispose();
                _activeRun = null;
                SetBusy(false);
                AgentStatusText.Text = _indexing ? "Indexing — chat available" : "Ready";
                try { await RefreshHistoryAsync(); }
                catch (Exception ex) { AddMessageBubble($"Could not refresh history: {ex.Message}", false); }
                await OpenPendingProjectAsync();
            }
        }

        private void AddMessageBubble(string message, bool isUser)
        {
            Border bubble = new Border
            {
                Background = isUser
                ? new SolidColorBrush(Color.FromRgb(76, 58, 105))
                : new SolidColorBrush(Color.FromRgb(42, 42, 42)),

                BorderBrush = isUser
                ? new SolidColorBrush(Color.FromRgb(118, 83, 181))
                : new SolidColorBrush(Color.FromRgb(58, 58, 58)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(11, 8, 11, 8),
                Margin = isUser
                ? new Thickness(48, 0, 0, 10)
                : new Thickness(0, 0, 28, 10),
            };

            TextBox text = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(216, 216, 216)),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 12,
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
                Title = "Select a project folder",
                InitialDirectory = GetProjectPickerInitialDirectory()
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string selectedPath = dialog.FolderName;
            string solutionOrProjectPath;
            try
            {
                solutionOrProjectPath = ProjectFolderResolver.FindSolution(selectedPath);
                selectedPath = Path.GetDirectoryName(solutionOrProjectPath)!;
            }
            catch (InvalidOperationException err)
            {
                var projectDialog = new OpenFileDialog
                {
                    Title = err.Message,
                    InitialDirectory = selectedPath,
                    Filter = "C# solutions and projects|*.sln;*.csproj",
                    CheckFileExists = true
                };
                if (projectDialog.ShowDialog(this) != true) return;
                solutionOrProjectPath = projectDialog.FileName;
                selectedPath = Path.GetDirectoryName(solutionOrProjectPath)!;
            }
            catch (Exception err) when (err is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(err.Message, "Cannot read project folder");
                return;
            }

            if (!_ready || _busy || _indexing)
            {
                _pendingProject = (selectedPath, solutionOrProjectPath);
                OpenProjectButton.Content = "Folder queued…";
                OpenProjectButton.ToolTip = $"Will open {selectedPath} when the current operation finishes. Click to choose another folder.";
                AgentStatusText.Text = $"Queued: {Path.GetFileName(selectedPath)}";
                return;
            }
            await OpenProjectAsync(selectedPath, solutionOrProjectPath);
        }

        private async Task OpenPendingProjectAsync()
        {
            if (!_ready || _busy || _indexing || _windowLifetime.IsCancellationRequested || _pendingProject is null) return;
            var pending = _pendingProject.Value;
            _pendingProject = null;
            await OpenProjectAsync(pending.ProjectPath, pending.SolutionPath);
        }

        private async Task DisplayProjectAsync(string selectedPath, string solutionOrProjectPath)
        {
            if (!Directory.Exists(selectedPath) || !File.Exists(solutionOrProjectPath))
                throw new IOException("The selected project folder or solution no longer exists.");

            _selectedProjectPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedPath));
            _selectedSolutionPath = solutionOrProjectPath;
            // Remember the selection even if indexing fails or the app closes during indexing.
            string? preferenceError = null;
            try
            {
                await _lastProjectStore.SaveAsync(selectedPath, solutionOrProjectPath, _windowLifetime.Token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                preferenceError = ex.Message;
            }

            FolderView.Items.Clear();
            TreeViewItem rootItem = BuildDisplayableExplorerDirectory(selectedPath, includeIfEmpty: true)!;
            FolderView.Items.Add(rootItem);
            rootItem.IsExpanded = true;
            _projectScanner = new ProjectScanner(selectedPath);
            _projectSearchService = new ProjectSearchService(_projectScanner);
            OpenProjectButton.Content = "Change folder";
            OpenProjectButton.ToolTip = _selectedProjectPath;
            ConversationPicker.ToolTip = $"Chat history for {_selectedProjectPath}";
            _activeConversationId = Guid.Empty;
            ChatMessagesPanel.Children.Clear();
            contextFiles.Clear();
            await RestoreProjectChatAsync();
            if (preferenceError is not null)
                AddMessageBubble($"Project opened, but its startup preference could not be saved: {preferenceError}", false);
        }

        private async Task OpenProjectAsync(string selectedPath, string solutionOrProjectPath, bool alreadyDisplayed = false)
        {
            bool chatReleased = false;
            try
            {
                SetBusy(true);
                _indexing = true;
                if (!alreadyDisplayed)
                    await DisplayProjectAsync(selectedPath, solutionOrProjectPath);
                AgentStatusText.Text = $"Indexing {Path.GetFileName(selectedPath)} — chat available";
                Task indexing = _mcpClient.IndexProjectAsync(selectedPath, solutionOrProjectPath, _windowLifetime.Token);
                SetBusy(false);
                chatReleased = true;
                await indexing;
                if (_activeRun is null) AgentStatusText.Text = $"Ready — {Path.GetFileName(selectedPath)}";
            }
            catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested) { }
            catch (Exception err)
            {
                AgentStatusText.Text = "Indexing failed — reopen project to retry";
                MessageBox.Show(
                    $"Lavender could not index the selected project.{Environment.NewLine}{Environment.NewLine}{err.Message}",
                    "Project indexing failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _indexing = false;
                // Index completion must not unlock controls owned by an active chat/history operation.
                if (!chatReleased) SetBusy(false);
                await OpenPendingProjectAsync();
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SendButton.IsEnabled = !busy || _activeRun is not null;
            SendButton.Content = _activeRun is not null ? "Stop" : "Send";
            ConversationPicker.IsEnabled = !busy;
            NewChatButton.IsEnabled = !busy;
            ClearHistoryButton.IsEnabled = !busy;
            OpenProjectButton.IsEnabled = true;
            UserInputBox.IsReadOnly = busy;
        }

        private async Task RestoreProjectChatAsync()
        {
            var history = await _conversations.ListAsync(_selectedProjectPath);
            Conversation chat = history.FirstOrDefault() ?? await CreateProjectChatAsync();
            ShowConversation(chat);
            await RefreshHistoryAsync();
        }

        private async Task<Conversation> CreateProjectChatAsync()
        {
            Conversation chat = await _conversations.CreateAsync();
            chat.ProjectPath = _selectedProjectPath;
            chat.SolutionPath = _selectedSolutionPath;
            await _conversations.SaveAsync(chat);
            return chat;
        }

        private void ShowConversation(Conversation chat)
        {
            AgentActivityLog.Clear();
            _activeConversationId = chat.Id;
            contextFiles.Clear();
            if (_selectedProjectPath is not null)
                contextFiles.AddRange(chat.ContextFiles.Where(path => File.Exists(path) &&
                    Path.GetFullPath(path).StartsWith(_selectedProjectPath + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)));
            SelectedContextText.Text = $"Context: {contextFiles.Count} file(s)";
            UserInputBox.Clear();
            ChatMessagesPanel.Children.Clear();
            foreach (ConversationTurn turn in chat.Turns)
            {
                foreach (var call in turn.Steps.SelectMany(step => step.ToolCalls)) AppendToolActivity(call);
                AddMessageBubble(turn.UserMessage, true);
                if (!string.IsNullOrWhiteSpace(turn.AssistantMessage)) AddMessageBubble(turn.AssistantMessage, false);
                else AddMessageBubble(turn.StopReason ?? "This response was interrupted. You can ask again.", false);
            }
        }

        private void AppendAgentActivity(string message)
        {
            AgentActivityLog.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\n");
            // Keep the visible log bounded; full tool records remain in chat history.
            if (AgentActivityLog.Text.Length > 60000)
                AgentActivityLog.Text = AgentActivityLog.Text[^50000..];
            AgentActivityLog.ScrollToEnd();
        }

        private void AppendToolActivity(ToolExecutionRecord record)
        {
            string target = "";
            try
            {
                using var args = System.Text.Json.JsonDocument.Parse(record.ArgumentsJson);
                foreach (string key in new[] { "path", "filePath", "query", "destinationPath", "startLine", "endLine" })
                    if (args.RootElement.TryGetProperty(key, out var value))
                    {
                        string text = value.ToString();
                        target += $" {key}={text[..Math.Min(text.Length, 250)]}";
                    }
            }
            catch (System.Text.Json.JsonException) { }
            string outcome = record.OutcomeMessage ?? record.Error ?? "Tool returned a result.";
            double seconds = ((record.CompletedAt ?? record.StartedAt) - record.StartedAt).TotalSeconds;
            string message = $"{record.ToolName}{target}\n  {record.Outcome ?? "returned"} ({seconds:F1}s): {outcome[..Math.Min(outcome.Length, 1000)]}";
            AppendAgentActivity(message);
        }

        private async Task RefreshHistoryAsync()
        {
            _refreshingHistory = true;
            try
            {
                var history = await _conversations.ListAsync(_selectedProjectPath);
                ConversationPicker.ItemsSource = history;
                ConversationPicker.SelectedItem = history.FirstOrDefault(c => c.Id == _activeConversationId);
            }
            finally { _refreshingHistory = false; }
        }

        private async void NewChat_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _busy) return;
            try
            {
                SetBusy(true);
                ShowConversation(await CreateProjectChatAsync());
                await RefreshHistoryAsync();
            }
            catch (Exception ex) { AddMessageBubble($"Could not create chat: {ex.Message}", false); }
            finally { SetBusy(false); await OpenPendingProjectAsync(); }
        }

        private void ConversationPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingHistory || _busy || !_ready) return;
            if (ConversationPicker.SelectedItem is Conversation chat) ShowConversation(chat);
        }

        private async void ClearContext_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _busy) return;
            contextFiles.Clear();
            await SaveContextFilesAsync();
        }

        private async void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _busy) return;
            try
            {
                SetBusy(true);
                await _conversations.ClearProjectAsync(_selectedProjectPath);
                ShowConversation(await CreateProjectChatAsync());
                await RefreshHistoryAsync();
            }
            catch (Exception ex) { AddMessageBubble($"Could not clear history: {ex.Message}", false); }
            finally { SetBusy(false); await OpenPendingProjectAsync(); }
        }

        private void OpenTimings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string directory = IndexingTimings.ReportDirectory;
                Directory.CreateDirectory(directory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Reports folder: {IndexingTimings.ReportDirectory}\n{ex.Message}", "Indexing timings");
            }
        }

        private async Task SaveContextFilesAsync()
        {
            SelectedContextText.Text = $"Context: {contextFiles.Count} file(s)";
            try
            {
                var chat = await _conversations.LoadAsync(_activeConversationId);
                if (chat is null) return;
                chat.ContextFiles = contextFiles.ToList();
                await _conversations.SaveAsync(chat);
            }
            catch (Exception ex) { AddMessageBubble($"Could not save selected files: {ex.Message}", false); }
        }

        private string GetProjectPickerInitialDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_selectedProjectPath))
            {
                string? parentPath = Directory.GetParent(_selectedProjectPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parentPath) && Directory.Exists(parentPath))
                {
                    return parentPath;
                }
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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

        private static TreeViewItem? BuildDisplayableExplorerDirectory(string directoryPath, bool includeIfEmpty = false)
        {
            if (!includeIfEmpty && ProjectScanner.ShouldIgnoreFolder(directoryPath))
            {
                return null;
            }

            try
            {
                if (File.GetAttributes(directoryPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    return null;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }

            var directoryItem = new TreeViewItem
            {
                Header = GetFileFolderName(directoryPath),
                Tag = directoryPath
            };

            foreach (string childDirectory in SafeEnumerateDirectories(directoryPath)
                         .OrderBy(GetFileFolderName, StringComparer.OrdinalIgnoreCase))
            {
                TreeViewItem? childItem = BuildDisplayableExplorerDirectory(childDirectory);
                if (childItem is not null)
                {
                    directoryItem.Items.Add(childItem);
                }
            }

            foreach (string filePath in SafeEnumerateFiles(directoryPath)
                         .Where(IsDisplayableExplorerFile)
                         .OrderBy(GetFileFolderName, StringComparer.OrdinalIgnoreCase))
            {
                directoryItem.Items.Add(new TreeViewItem
                {
                    Header = GetFileFolderName(filePath),
                    Tag = filePath
                });
            }

            return includeIfEmpty || directoryItem.Items.Count > 0
                ? directoryItem
                : null;
        }

        private static bool IsDisplayableExplorerFile(string filePath)
        {
            if (ProjectScanner.ShouldIgnoreFile(filePath))
            {
                return false;
            }

            string extension = Path.GetExtension(filePath);
            string fileName = Path.GetFileName(filePath);

            return ExplorerFileExtensions.Contains(extension)
                || ExplorerFileNames.Contains(fileName);
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string directoryPath)
        {
            try
            {
                return Directory.EnumerateDirectories(directoryPath).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string directoryPath)
        {
            try
            {
                return Directory.EnumerateFiles(directoryPath).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
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

            if (File.Exists(path) && IsDisplayableExplorerFile(path))
            {
                var tab = _openFiles.FirstOrDefault(t => string.Equals(t.FullPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                if (tab == null) { tab = new OpenFileTab(path); _openFiles.Add(tab); }
                OpenFileTabs.SelectedItem = tab;
            }
        }

        private void OpenFileTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (OpenFileTabs.SelectedItem is not OpenFileTab tab || ReferenceEquals(tab, _activeFile)) return;
            if (_activeFile != null)
            {
                _activeFile.CaretOffset = FilePreviewBox.CaretOffset;
                _activeFile.VerticalOffset = FilePreviewBox.VerticalOffset;
                _activeFile.HorizontalOffset = FilePreviewBox.HorizontalOffset;
            }
            _activeFile = tab;
            currSelectedFile = tab.FullPath;
            FilePreviewBox.Document = tab.Document;
            FilePreviewBox.Encoding = tab.Encoding;
            ShowCodeInPreview(tab.Document.Text);
            FilePreviewBox.CaretOffset = Math.Min(tab.CaretOffset, tab.Document.TextLength);
            FilePreviewBox.ScrollToVerticalOffset(tab.VerticalOffset);
            FilePreviewBox.ScrollToHorizontalOffset(tab.HorizontalOffset);
            var previous = _recentFiles.Find(tab.FullPath);
            if (previous != null) _recentFiles.Remove(previous);
            _recentFiles.AddFirst(tab.FullPath);
        }

        private void ConfigureCodeEditor()
        {
            // Space between the line-number gutter and the first code character.
            FilePreviewBox.TextArea.LeftMargins.Add(new Border { Width = 8 });
            // Handle this above the editor's own command bindings, regardless of focus.
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    SaveCurrentFile();
                }
            };
            FilePreviewBox.Options.HighlightCurrentLine = true;
            FilePreviewBox.TextArea.TextView.CurrentLineBackground =
                new SolidColorBrush(Color.FromRgb(21, 21, 21));
            FilePreviewBox.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
            FilePreviewBox.TextArea.SelectionBorder = new Pen(Brushes.Transparent, 0);
            FilePreviewBox.TextArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(58, 45, 76));
            FilePreviewBox.TextArea.SelectionForeground = Brushes.White;
        }

        private void SaveFile_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = FilePreviewBox != null && currSelectedFile != null && File.Exists(currSelectedFile);
            e.Handled = true;
        }

        private void SaveFile_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            SaveCurrentFile();
        }

        private void SaveCurrentFile()
        {
            if (currSelectedFile == null || !File.Exists(currSelectedFile)) return;
            try
            {
                // Match the existing file's BOM/encoding; save without reloading the editor.
                using (var reader = new StreamReader(currSelectedFile, new System.Text.UTF8Encoding(false), true))
                {
                    reader.Peek();
                    FilePreviewBox.Encoding = reader.CurrentEncoding;
                }
                FilePreviewBox.Save(currSelectedFile);
                FilePreviewBox.IsModified = false;
                _activeFile?.MarkSaved();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                MessageBox.Show(this, $"Could not save {currSelectedFile}:\n{ex.Message}",
                    "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowCodeInPreview(string code)
        {
            // Replacing text is only for opening/reloading a file, never for typing.
            var definition = ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance
                .GetDefinitionByExtension(Path.GetExtension(currSelectedFile ?? ""));
            if (definition != null)
            {
                foreach (var color in definition.NamedHighlightingColors)
                {
                    string name = color.Name ?? "";
                    string hex = name.Contains("Comment", StringComparison.OrdinalIgnoreCase) ? "#6A9955"
                        : name.Contains("String", StringComparison.OrdinalIgnoreCase) || name.Contains("Char", StringComparison.OrdinalIgnoreCase) ? "#CE9178"
                        : name.Contains("Number", StringComparison.OrdinalIgnoreCase) || name.Contains("Digit", StringComparison.OrdinalIgnoreCase) ? "#B5CEA8"
                        : name.Contains("Keyword", StringComparison.OrdinalIgnoreCase) ? "#C084FC"
                        : "#D4D4D4";
                    color.Foreground = new ICSharpCode.AvalonEdit.Highlighting.SimpleHighlightingBrush(
                        (Color)ColorConverter.ConvertFromString(hex));
                }
            }
            FilePreviewBox.SyntaxHighlighting = definition;
            // The selected tab owns the document; switching never replaces its text.
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

            if (!IsDisplayableExplorerFile(path))
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

        private async void ChatPanel_Drop(object sender, DragEventArgs e)
        {
            if (!_ready || _busy || _selectedProjectPath is null) return;
            if (!e.Data.GetDataPresent(DataFormats.StringFormat))
                return;

            string? path = e.Data.GetData(DataFormats.StringFormat) as string;

            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!File.Exists(path))
                return;

            if (!Path.GetFullPath(path).StartsWith(_selectedProjectPath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) return;

            if (!IsDisplayableExplorerFile(path))
                return;

            if (!contextFiles.Contains(path))
                contextFiles.Add(path);

            SelectedContextText.Text = $"Context: {contextFiles.Count} file(s)";
            await SaveContextFilesAsync();
        }


        #endregion

    }
}



