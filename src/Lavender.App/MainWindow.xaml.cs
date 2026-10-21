using Lavender.App.Rendering;
using Lavender.Application.Agent;
using Lavender.Infrastructure.AI;
using Lavender.Infrastructure.Backend;
using Lavender.Infrastructure.FileSystem;
using Lavender.Infrastructure.Indexing;
using Lavender.Infrastructure.Mcp;
using Lavender.Infrastructure.Retrieval;
using Microsoft.Win32;
using System.IO;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lavender.App
{
    /// <summary>Coordinates the project workspace, editor, and assistant chat.</summary>
    public partial class MainWindow : Window
    {
        #region Fields and workspace state

        // Editor documents and navigation.
        private string? currSelectedFile;
        private readonly System.Collections.ObjectModel.ObservableCollection<OpenFileTab> _openFiles = new();
        private readonly LinkedList<string> _recentFiles = new(); // Most recent path first; no duplicates.
        private OpenFileTab? _activeFile;
        private readonly EditorAssistance _editorAssistance;
        private CancellationTokenSource? _openFileRequest;
        private bool _settingFileSelection;

        // Selected project and explorer filters.
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

        // These services are created after a project directory is selected.
        private ProjectScanner? _projectScanner;
        private ProjectSearchService? _projectSearchService;

        // Chat services and operation state.
        private readonly AgentRunner _agentRunner;
        // Adjust this interval to change the visible reply speed.
        private const int ReplyCharacterIntervalMs = 2;
        private readonly DispatcherTimer _replyRevealTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(ReplyCharacterIntervalMs)
        };
        private readonly StringBuilder _pendingReply = new();
        private readonly StringBuilder _visibleReply = new();
        private TaskCompletionSource<bool>? _replyDrained;
        private bool _replyStreamComplete;
        private RichTextBox? _streamingReplyText;
        private readonly OpenAIService _model;
        private IReadOnlyList<AgentModelOption> _availableModels = [];
        private AgentModelOption _selectedModel = null!;
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

        #endregion

        #region Startup and shutdown

        /// <summary>
        /// Default constructor
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            ChatMessagesPanel.SizeChanged += (_, _) => ResizeUserMessages();
            _replyRevealTimer.Tick += RevealNextReplyCharacter;
            ConfigureCodeEditor();
            _editorAssistance = new EditorAssistance(
                FilePreviewBox,
                () => _openFiles.Where(tab => !tab.IsImage).ToDictionary(tab => tab.FullPath, tab => tab.Document.Text,
                    StringComparer.OrdinalIgnoreCase),
                AppendAgentActivity,
                loading =>
                {
                    FilePreviewBox.Opacity = loading ? 0 : 1;
                    FilePreviewBox.IsEnabled = !loading;
                    EditorLoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
                });
            OpenFileTabs.ItemsSource = _openFiles;

            AgentSettings settings = AgentSettings.Load();
            settings.Validate();
            _mcpClient = new LavenderMcpClient(FindRepositoryRoot());
            _conversations = new(settings.HistoryDirectory, settings.PersistHistory);
            _model = new(settings.Models.First(model => model.Id == settings.Model));
            _availableModels = settings.Models;
            _selectedModel = settings.Models.First(model => model.Id == settings.Model);
            RefreshModelPickerLabel();
            _agentRunner = new AgentRunner(settings, _model, _mcpClient, _conversations);

            _agentRunner.Progress += status => Dispatcher.InvokeAsync(() =>
            {
                AgentStatusText.Text = status;
                AppendAgentActivity(status);
            });
            _agentRunner.ResponseDelta += delta =>
            {
                if (Dispatcher.CheckAccess())
                    AppendResponseDelta(delta);
                else
                    Dispatcher.Invoke(() => AppendResponseDelta(delta));
            };
            _agentRunner.ToolCompleted += record => Dispatcher.InvokeAsync(() => AppendToolActivity(record));

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await InitializeAsync();
            }
            catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Lavender startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
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

        /// <summary>
        /// Event handler for when the window first loads
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
        }

        protected override async void OnClosed(EventArgs e)
        {
            _windowLifetime.Cancel();
            _activeRun?.Cancel();
            try
            {
                FastApiService.Instance.StopServer();
            }
            finally
            {
                try
                {
                    await _editorAssistance.DisposeAsync();
                    await _mcpClient.DisposeAsync();
                }
                finally
                {
                    base.OnClosed(e);
                }
            }
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

            throw new DirectoryNotFoundException("Could not locate the Lavender repository root.");
        }

        #endregion

        #region Project selection and indexing

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
                // Keep the chosen workspace root, including assets beside a nested project.
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
                if (projectDialog.ShowDialog(this) != true)
                    return;
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
                OpenProjectLabel.Text = "Project queued…";
                OpenProjectButton.ToolTip = $"Will open {selectedPath} when the current operation finishes. Click to choose another folder.";
                AgentStatusText.Text = $"Queued: {Path.GetFileName(selectedPath)}";
                return;
            }

            await OpenProjectAsync(selectedPath, solutionOrProjectPath);
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

        private async Task OpenPendingProjectAsync()
        {
            if (!_ready || _busy || _indexing || _windowLifetime.IsCancellationRequested || _pendingProject is null)
                return;
            var pending = _pendingProject.Value;
            _pendingProject = null;
            await OpenProjectAsync(pending.ProjectPath, pending.SolutionPath);
        }

        private async Task OpenProjectAsync(
            string selectedPath,
            string solutionOrProjectPath,
            bool alreadyDisplayed = false,
            Conversation? conversation = null)
        {
            bool chatReleased = false;
            try
            {
                SetBusy(true);
                _indexing = true;
                if (!alreadyDisplayed)
                {
                    await DisplayProjectAsync(selectedPath, solutionOrProjectPath, conversation);
                }

                AgentStatusText.Text = $"Indexing {Path.GetFileName(selectedPath)} — chat available";
                Task indexing = _mcpClient.IndexProjectAsync(selectedPath, solutionOrProjectPath, _windowLifetime.Token);
                SetBusy(false);
                chatReleased = true;
                await indexing;
                if (_activeRun is null)
                    AgentStatusText.Text = $"Ready — {Path.GetFileName(selectedPath)}";
            }
            catch (OperationCanceledException) when (_windowLifetime.IsCancellationRequested)
            {
            }
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
                if (!chatReleased)
                    SetBusy(false);
                await OpenPendingProjectAsync();
            }
        }

        private async Task DisplayProjectAsync(string selectedPath, string solutionOrProjectPath, Conversation? conversation = null)
        {
            _openFileRequest?.Cancel();
            if (!Directory.Exists(selectedPath) || !File.Exists(solutionOrProjectPath))
                throw new IOException("The selected project folder or solution no longer exists.");
            _selectedProjectPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedPath));
            _selectedSolutionPath = solutionOrProjectPath;
            _editorAssistance.SelectFile(_selectedSolutionPath, currSelectedFile, reloadProject: true);
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
            OpenProjectLabel.Text = "Open project";
            OpenProjectButton.ToolTip = _selectedProjectPath;
            ConversationPicker.ToolTip = $"Previous chats for {_selectedProjectPath}";
            _activeConversationId = Guid.Empty;
            ChatMessagesPanel.Children.Clear();
            contextFiles.Clear();
            UpdateContextDisplay();
            if (conversation is null) await RestoreProjectChatAsync();
            else
            {
                ShowConversation(conversation);
                await RefreshHistoryAsync();
            }
            if (preferenceError is not null)
                AddMessageBubble($"Project opened, but its startup preference could not be saved: {preferenceError}", false);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            SendButton.IsEnabled = !busy || _activeRun is not null;
            SendButton.Content = _activeRun is not null
                ? new System.Windows.Shapes.Rectangle { Width = 11, Height = 11, RadiusX = 2, RadiusY = 2, Fill = Brushes.White }
                : new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 2,12 L 10,4 L 18,12 M 10,4 L 10,20"),
                    Width = 15, Height = 15, Stretch = Stretch.Uniform,
                    Stroke = Brushes.White, StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };
            SendButton.ToolTip = _activeRun is not null ? "Stop response" : "Send message (Enter)";
            System.Windows.Automation.AutomationProperties.SetName(SendButton,
                _activeRun is not null ? "Stop response" : "Send message");
            ModelPicker.IsEnabled = !busy;
            if (busy)
                ModelMenuPopup.IsOpen = false;
            AttachFilesButton.IsEnabled = !busy;
            ConversationPicker.IsEnabled = !busy;
            NewChatButton.IsEnabled = !busy;
            ClearHistoryButton.IsEnabled = !busy;
            OpenProjectButton.IsEnabled = true;
            UserInputBox.IsReadOnly = busy;
        }

        #endregion

        #region Chat requests and messages

        private void ModelPicker_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            ShowModelChoices();
            ModelMenuPopup.IsOpen = true;
        }

        private void ModelMenuPopup_Closed(object sender, EventArgs e)
        {
            ModelMenu.Visibility = Visibility.Visible;
            ReasoningMenu.Visibility = Visibility.Collapsed;
        }

        private void ShowModelChoices()
        {
            ModelListPanel.Children.Clear();
            foreach (AgentModelOption model in _availableModels)
            {
                Button option = CreateMenuOption(model.Label, model == _selectedModel,
                    () => SelectModelFromMenu(model));
                option.ToolTip = model.AllowedReasoningLevels.Count > 1
                    ? "Choose a reasoning level after selecting this model." : model.ReasoningHint;
                ModelListPanel.Children.Add(option);
            }
            ModelMenu.Visibility = Visibility.Visible;
            ReasoningMenu.Visibility = Visibility.Collapsed;
        }

        private void SelectModelFromMenu(AgentModelOption model)
        {
            if (_busy)
                return;
            _selectedModel = model;
            RefreshModelPickerLabel();
            if (model.AllowedReasoningLevels.Count <= 1)
            {
                ModelMenuPopup.IsOpen = false;
                return;
            }

            ReasoningListPanel.Children.Clear();
            foreach (ReasoningEffort? level in model.AllowedReasoningLevels)
            {
                string label = level?.ToString() ?? "Default";
                Button option = CreateMenuOption(label, level == model.ReasoningEffort, () =>
                {
                    model.ReasoningEffort = level;
                    RefreshModelPickerLabel();
                    ModelMenuPopup.IsOpen = false;
                });
                option.ToolTip = model.ReasoningHint;
                System.Windows.Automation.AutomationProperties.SetName(option, $"{model.Label} reasoning: {label}");
                ReasoningListPanel.Children.Add(option);
            }
            ModelMenu.Visibility = Visibility.Collapsed;
            ReasoningMenu.Visibility = Visibility.Visible;
        }

        private void ReasoningBack_Click(object sender, RoutedEventArgs e) => ShowModelChoices();

        private void RefreshModelPickerLabel()
        {
            ModelPickerLabel.Text = _selectedModel.Label;
            bool showReasoning = _selectedModel.AllowedReasoningLevels.Count > 1 &&
                _selectedModel.ReasoningEffort != ReasoningEffort.None;
            ModelPickerReasoningLabel.Text = showReasoning
                ? _selectedModel.ReasoningEffort?.ToString() ?? "Default" : "";
            ModelPickerReasoningLabel.Visibility = showReasoning
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private static Button CreateMenuOption(string label, bool selected, Action choose)
        {
            Grid content = new();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            if (selected)
            {
                TextBlock check = new()
                {
                    Text = "✓",
                    Foreground = new SolidColorBrush(Color.FromRgb(187, 158, 232)),
                    Margin = new Thickness(12, 0, 0, 0)
                };
                Grid.SetColumn(check, 1);
                content.Children.Add(check);
            }

            Button option = new()
            {
                Content = content,
                Background = selected ? new SolidColorBrush(Color.FromRgb(73, 56, 94)) : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 7, 10, 7),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 32,
                MinWidth = 212,
                Margin = new Thickness(0, 1, 0, 1)
            };
            System.Windows.Automation.AutomationProperties.SetName(option, label);
            option.Click += (_, _) => choose();
            return option;
        }

        /// <summary>
        /// Event handler for when the user sends a request to OpenAI API
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeRun is not null)
            {
                _activeRun.Cancel();
                return;
            }

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
            if (!_ready || _busy)
                return;
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
            _streamingReplyText = null;
            _pendingReply.Clear();
            _visibleReply.Clear();
            _replyStreamComplete = false;
            _replyDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _model.SelectModel(_selectedModel);
                _activeRun = new CancellationTokenSource();
                SetBusy(true);
                AgentRunResult result = await _agentRunner.RunAsync(_activeConversationId, input, BuildProjectContext(), _activeRun.Token);
                foreach (string diagnostic in result.ToolDiagnostics ?? [])
                {
                    // Lets ignore this for now
                    // AddMessageBubble($"Tool error: {diagnostic}", false);
                }

                _replyStreamComplete = true;
                if (_pendingReply.Length == 0)
                    _replyDrained.TrySetResult(true);
                await _replyDrained.Task.WaitAsync(_activeRun.Token);
                _streamingReplyText ??= AddAssistantMessage(result.FinalAnswer);
                AssistantMarkdown.Render(_streamingReplyText, result.FinalAnswer);
                if (result.ProjectFilesChanged && _selectedProjectPath is not null)
                {
                    FolderView.Items.Clear();
                    TreeViewItem? root = BuildDisplayableExplorerDirectory(_selectedProjectPath, includeIfEmpty: true);
                    if (root is not null)
                    {
                        FolderView.Items.Add(root);
                        root.IsExpanded = true;
                    }

                    foreach (var tab in _openFiles)
                        tab.ReloadIfClean();
                    _editorAssistance.ReloadProject();
                    if (_activeFile is { IsImage: true } imageTab)
                        OpenPreparedFile(imageTab);
                    if (contextFiles.RemoveAll(path => !File.Exists(path)) > 0)
                        await SaveContextFilesAsync();
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
                _replyRevealTimer.Stop();
                _pendingReply.Clear();
                _streamingReplyText = null;
                _replyDrained = null;
                SetBusy(false);
                AgentStatusText.Text = _indexing ? "Indexing — chat available" : "Ready";
                try
                {
                    await RefreshHistoryAsync();
                }
                catch (Exception ex)
                {
                    AddMessageBubble($"Could not refresh history: {ex.Message}", false);
                }

                await OpenPendingProjectAsync();
            }
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

        private void AddMessageBubble(string message, bool isUser)
        {
            if (!isUser)
            {
                AddAssistantMessage(message);
                return;
            }

            TextBox text = new TextBox
            {
                Text = message,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(216, 216, 216)),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };
            text.HorizontalAlignment = HorizontalAlignment.Left;
            Border bubble = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(76, 58, 105)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 2, 14),
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = "UserMessage"
            };
            bubble.MaxWidth = Math.Max(160, ChatMessagesPanel.ActualWidth * 0.7);
            bubble.Child = text;
            ChatMessagesPanel.Children.Add(bubble);
            ChatScrollViewer.ScrollToEnd();
        }

        private RichTextBox AddAssistantMessage(string markdown)
        {
            RichTextBox text = new()
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 6),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Foreground = new SolidColorBrush(Color.FromRgb(216, 216, 216)),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 12,
                Margin = new Thickness(2, 0, 8, 16)
            };
            text.Document.PagePadding = new Thickness(0);
            ContextMenu menu = new();
            menu.Items.Add(new MenuItem { Header = "Copy selected text",
                Command = ApplicationCommands.Copy, CommandTarget = text });
            MenuItem copyAll = new() { Header = "Copy entire response" };
            copyAll.Click += (_, _) =>
            {
                if (text.Tag is string response && response.Length > 0)
                {
                    try { Clipboard.SetText(response); }
                    catch (System.Runtime.InteropServices.ExternalException ex)
                    { AppendAgentActivity($"Could not copy response: {ex.Message}"); }
                }
            };
            menu.Items.Add(copyAll);
            text.ContextMenu = menu;
            AssistantMarkdown.Render(text, markdown);
            ChatMessagesPanel.Children.Add(text);
            ChatScrollViewer.ScrollToEnd();
            return text;
        }

        private void AppendResponseDelta(string delta)
        {
            _streamingReplyText ??= AddAssistantMessage("");
            _pendingReply.Append(delta);
            if (!_replyRevealTimer.IsEnabled)
                _replyRevealTimer.Start();
        }

        private void RevealNextReplyCharacter(object? sender, EventArgs e)
        {
            if (_pendingReply.Length > 0 && _streamingReplyText is not null)
            {
                string next = StringInfo.GetNextTextElement(_pendingReply.ToString());
                _pendingReply.Remove(0, next.Length);
                _visibleReply.Append(next);
                AssistantMarkdown.Render(_streamingReplyText, _visibleReply.ToString());
            }
            ChatScrollViewer.ScrollToEnd();
            if (_pendingReply.Length == 0)
            {
                _replyRevealTimer.Stop();
                if (_replyStreamComplete)
                    _replyDrained?.TrySetResult(true);
            }
        }

        private void ResizeUserMessages()
        {
            double maxWidth = Math.Max(160, ChatMessagesPanel.ActualWidth * 0.7);
            foreach (Border bubble in ChatMessagesPanel.Children.OfType<Border>()
                .Where(border => Equals(border.Tag, "UserMessage")))
                bubble.MaxWidth = maxWidth;
        }

        #endregion

        #region Conversation history

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
                    Path.GetFullPath(path).StartsWith(
                        _selectedProjectPath + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)));
            UpdateContextDisplay();
            UserInputBox.Clear();
            ChatMessagesPanel.Children.Clear();
            foreach (ConversationTurn turn in chat.Turns)
            {
                foreach (var call in turn.Steps.SelectMany(step => step.ToolCalls))
                    AppendToolActivity(call);
                AddMessageBubble(turn.UserMessage, true);
                if (!string.IsNullOrWhiteSpace(turn.AssistantMessage))
                    AddMessageBubble(turn.AssistantMessage, false);
                else
                    AddMessageBubble(turn.StopReason ?? "This response was interrupted. You can ask again.", false);
            }
        }

        private async Task RefreshHistoryAsync()
        {
            _refreshingHistory = true;
            try
            {
                var history = await _conversations.ListAsync(_selectedProjectPath, _windowLifetime.Token);
                ConversationPicker.ItemsSource = history;
                ConversationPicker.SelectedItem = history.FirstOrDefault(c => c.Id == _activeConversationId);
            }
            finally
            {
                _refreshingHistory = false;
            }
        }

        private async void NewChat_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _busy)
                return;
            try
            {
                SetBusy(true);
                ShowConversation(await CreateProjectChatAsync());
                await RefreshHistoryAsync();
            }
            catch (Exception ex)
            {
                AddMessageBubble($"Could not create chat: {ex.Message}", false);
            }
            finally
            {
                SetBusy(false);
                await OpenPendingProjectAsync();
            }
        }

        private void ConversationPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingHistory || _busy || !_ready)
                return;
            if (ConversationPicker.SelectedItem is Conversation chat && chat.Id != _activeConversationId &&
                string.Equals(JsonConversationStore.NormalizeProjectPath(chat.ProjectPath),
                    JsonConversationStore.NormalizeProjectPath(_selectedProjectPath), StringComparison.OrdinalIgnoreCase))
                ShowConversation(chat);
        }

        private async void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _busy)
                return;
            if (MessageBox.Show("Delete all saved chats for the currently open project?", "Clear project chats",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                SetBusy(true);
                await _conversations.ClearProjectAsync(_selectedProjectPath);
                ShowConversation(await CreateProjectChatAsync());
                await RefreshHistoryAsync();
            }
            catch (Exception ex)
            {
                AddMessageBubble($"Could not clear history: {ex.Message}", false);
            }
            finally
            {
                SetBusy(false);
                await OpenPendingProjectAsync();
            }
        }

        #endregion

        #region Selected context files and drag-and-drop

        private async Task SaveContextFilesAsync()
        {
            UpdateContextDisplay();
            try
            {
                var chat = await _conversations.LoadAsync(_activeConversationId);
                if (chat is null)
                    return;
                chat.ContextFiles = contextFiles.ToList();
                await _conversations.SaveAsync(chat);
            }
            catch (Exception ex)
            {
                AddMessageBubble($"Could not save selected files: {ex.Message}", false);
            }
        }

        private void UpdateContextDisplay()
        {
            ContextFilesRow.Visibility = contextFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SelectedContextText.Text = $"Context: {contextFiles.Count} file(s)";
        }

        private async void ClearContext_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _busy)
                return;
            contextFiles.Clear();
            await SaveContextFilesAsync();
        }

        private async void AttachFiles_Click(object sender, RoutedEventArgs e)
        {
            if (!_ready || _busy)
                return;
            if (_selectedProjectPath is null)
            {
                AddMessageBubble("Open a project before adding context files.", false);
                return;
            }
            OpenFileDialog dialog = new() { Title = "Add project files to chat", Multiselect = true,
                InitialDirectory = _selectedProjectPath, Filter = "All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true)
                return;
            foreach (string path in dialog.FileNames)
            {
                if (Path.GetFullPath(path).StartsWith(_selectedProjectPath + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) && IsDisplayableExplorerFile(path))
                {
                    if (!contextFiles.Contains(path)) contextFiles.Add(path);
                }
                else
                    AddMessageBubble($"Choose a supported source or text file inside the open project: {Path.GetFileName(path)}", false);
            }
            await SaveContextFilesAsync();
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
            if (!_ready || _busy || _selectedProjectPath is null)
                return;
            if (!e.Data.GetDataPresent(DataFormats.StringFormat))
                return;
            string? path = e.Data.GetData(DataFormats.StringFormat) as string;
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (!File.Exists(path))
                return;
            if (!Path.GetFullPath(path).StartsWith(_selectedProjectPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;
            if (!IsDisplayableExplorerFile(path))
                return;
            if (!contextFiles.Contains(path))
                contextFiles.Add(path);
            UpdateContextDisplay();
            await SaveContextFilesAsync();
        }

        #endregion

        #region File explorer

        private static TreeViewItem? BuildDisplayableExplorerDirectory(
            string directoryPath,
            bool includeIfEmpty = false)
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
            foreach (string childDirectory in SafeEnumerateDirectories(directoryPath).OrderBy(GetFileFolderName, StringComparer.OrdinalIgnoreCase))
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
                directoryItem.Items.Add(new TreeViewItem { Header = GetFileFolderName(filePath), Tag = filePath });
            }

            return includeIfEmpty || directoryItem.Items.Count > 0 ? directoryItem : null;
        }

        private static bool IsDisplayableExplorerFile(string filePath)
        {
            if (ProjectScanner.ShouldIgnoreFile(filePath))
            {
                return false;
            }

            string extension = Path.GetExtension(filePath);
            string fileName = Path.GetFileName(filePath);
            return ImageAssets.IsImage(filePath) || ExplorerFileExtensions.Contains(extension) || ExplorerFileNames.Contains(fileName);
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

        /// <summary>Prepares a selected explorer file before adding or activating its editor tab.</summary>
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
                if (tab == null)
                {
                    tab = new OpenFileTab(path);
                }

                OpenPreparedFile(tab);
            }
        }

        #endregion

        #region Editor tabs, saving, and highlighting

        private void CodeWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Keep the fixed-width explorer from pushing the editor past its container
            // when the user narrows the window or drags the chat/workspace divider.
            if (ExplorerColumn is null || CodeEditorColumn is null || e.NewSize.Width <= 0)
                return;
            ExplorerColumn.MaxWidth = Math.Max(ExplorerColumn.MinWidth,
                e.NewSize.Width - 6 - CodeEditorColumn.MinWidth);
        }

        private void AgentEditorSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            ResizeAgentEditor(e.HorizontalChange);
            e.Handled = true;
        }

        private void AgentEditorSplitter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Left or Key.Right))
                return;
            ResizeAgentEditor(e.Key == Key.Left ? -10 : 10);
            e.Handled = true;
        }

        private void ResizeAgentEditor(double delta)
        {
            var agent = WorkspacePanels.ColumnDefinitions[2];
            var editor = WorkspacePanels.ColumnDefinitions[4];
            double total = agent.ActualWidth + editor.ActualWidth;
            if (total <= agent.MinWidth + editor.MinWidth)
                return;
            double width = Math.Clamp(agent.ActualWidth + delta, agent.MinWidth, total - editor.MinWidth);
            // Preserve proportional resizing when the window itself changes size.
            agent.Width = new GridLength(width, GridUnitType.Star);
            editor.Width = new GridLength(total - width, GridUnitType.Star);
        }

        /// <summary>Configures editor appearance and the window-level save shortcut.</summary>
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
            FilePreviewBox.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromRgb(22, 22, 22));
            FilePreviewBox.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
            FilePreviewBox.TextArea.SelectionBorder = new Pen(Brushes.Transparent, 0);
            FilePreviewBox.TextArea.SelectionBrush = new SolidColorBrush(Color.FromRgb(58, 45, 76));
            FilePreviewBox.TextArea.SelectionForeground = Brushes.White;
        }

        /// <summary>Keeps the current tab active while preparing the requested tab.</summary>
        private void OpenFileTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingFileSelection || OpenFileTabs.SelectedItem is not OpenFileTab tab)
                return;
            // Keep the current document and selected tab together until the new one is ready.
            _settingFileSelection = true;
            try { OpenFileTabs.SelectedItem = _activeFile; }
            finally { _settingFileSelection = false; }
            OpenPreparedFile(tab);
        }

        /// <summary>Cancels obsolete navigation and prepares a current snapshot before switching tabs.</summary>
        private async void OpenPreparedFile(OpenFileTab tab)
        {
            _openFileRequest?.Cancel();
            if (ReferenceEquals(tab, _activeFile) && !tab.IsImage)
                return;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
            _openFileRequest = request;
            try
            {
                if (tab.IsImage)
                {
                    ImageSource? image = null;
                    string description;
                    try
                    {
                        (image, description) = await Task.Run(() => ImageAssets.Load(tab.FullPath), request.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        description = $"Could not preview {tab.FileName}: {ex.Message}";
                    }
                    request.Token.ThrowIfCancellationRequested();
                    ActivatePreparedFile(tab, []);
                    AssetPreviewImage.Source = image;
                    ImagePreviewStatus.Text = description;
                    return;
                }
                string? project = _selectedSolutionPath;
                Microsoft.CodeAnalysis.Classification.ClassifiedSpan[] spans;
                while (true)
                {
                    int revision = _editorAssistance.Revision;
                    string text = tab.Document.Text;
                    try
                    {
                        spans = await _editorAssistance.PrepareFileAsync(project, tab.FullPath, text, request.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        AppendAgentActivity($"Editor highlighting: {ex.Message}; opening with basic highlighting.");
                        spans = Array.Empty<Microsoft.CodeAnalysis.Classification.ClassifiedSpan>();
                    }
                    request.Token.ThrowIfCancellationRequested();
                    if (project != _selectedSolutionPath)
                        return;
                    // The visible file remains editable while the next tab is prepared.
                    if (revision == _editorAssistance.Revision && text == tab.Document.Text)
                        break;
                }
                ActivatePreparedFile(tab, spans);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppendAgentActivity($"Could not open {tab.FileName}: {ex.Message}"); }
            finally
            {
                if (ReferenceEquals(_openFileRequest, request))
                    _openFileRequest = null;
            }
        }

        /// <summary>Atomically switches the selected tab, document, and prepared highlighting while restoring its view.</summary>
        private void ActivatePreparedFile(OpenFileTab tab, Microsoft.CodeAnalysis.Classification.ClassifiedSpan[] spans)
        {
            if (_activeFile is { IsImage: false })
            {
                _activeFile.CaretOffset = FilePreviewBox.CaretOffset;
                _activeFile.VerticalOffset = FilePreviewBox.VerticalOffset;
                _activeFile.HorizontalOffset = FilePreviewBox.HorizontalOffset;
            }

            _activeFile = tab;
            _settingFileSelection = true;
            try
            {
                if (!_openFiles.Contains(tab))
                    _openFiles.Add(tab);
                OpenFileTabs.SelectedItem = tab;
            }
            finally { _settingFileSelection = false; }
            currSelectedFile = tab.FullPath;
            FilePreviewBox.Visibility = tab.IsImage ? Visibility.Collapsed : Visibility.Visible;
            ImagePreviewPanel.Visibility = tab.IsImage ? Visibility.Visible : Visibility.Collapsed;
            AssetPreviewImage.Source = null;
            FilePreviewBox.Document = tab.Document;
            FilePreviewBox.Encoding = tab.Encoding;
            _editorAssistance.SelectFile(_selectedSolutionPath, tab.IsImage ? null : tab.FullPath, preparedSpans: spans);
            FilePreviewBox.CaretOffset = Math.Min(tab.CaretOffset, tab.Document.TextLength);
            FilePreviewBox.ScrollToVerticalOffset(tab.VerticalOffset);
            FilePreviewBox.ScrollToHorizontalOffset(tab.HorizontalOffset);
            var previous = _recentFiles.Find(tab.FullPath);
            if (previous != null)
                _recentFiles.Remove(previous);
            _recentFiles.AddFirst(tab.FullPath);
        }

        private void SaveFile_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = _activeFile is { IsImage: false } && currSelectedFile != null && File.Exists(currSelectedFile);
            e.Handled = true;
        }

        private void SaveFile_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            e.Handled = true;
            SaveCurrentFile();
        }

        private void SaveCurrentFile()
        {
            if (_activeFile is not { IsImage: false } || currSelectedFile == null || !File.Exists(currSelectedFile))
                return;
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
                MessageBox.Show(this, $"Could not save {currSelectedFile}:\n{ex.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Diagnostics, activity logging, and timings

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
            catch (System.Text.Json.JsonException)
            {
            }

            string outcome = record.OutcomeMessage ?? record.Error ?? "Tool returned a result.";
            double seconds = ((record.CompletedAt ?? record.StartedAt) - record.StartedAt).TotalSeconds;
            string message = $"{record.ToolName}{target}\n  {record.Outcome ?? "returned"} ({seconds:F1}s): {outcome[..Math.Min(outcome.Length, 1000)]}";
            AppendAgentActivity(message);
        }

        private void OpenTimings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string directory = IndexingTimings.ReportDirectory;
                Directory.CreateDirectory(directory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Reports folder: {IndexingTimings.ReportDirectory}\n{ex.Message}", "Indexing timings");
            }
        }

        #endregion
    }
}
