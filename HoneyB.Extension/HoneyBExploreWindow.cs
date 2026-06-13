using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Linq;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

namespace HoneyB
{
    // ── Evaluated watch entry (runtime, not persisted) ────────────────────────
    public class WatchEvalResult
    {
        public string Path     { get; set; }
        public string Label    { get; set; }
        public string Value    { get; set; }   // null = not in scope
        public string SeenIn   { get; set; }
        public string CapturedAt { get; set; } // function.file:line
        public bool   Changed  { get; set; }
    }

    /// <summary>
    /// The main HoneyB tool window — three tabs: Chat, Explore, Watch.
    /// </summary>
    [Guid("e1f2a3b4-c5d6-7890-efab-cd1234567890")]
    public class HoneyBExploreWindow : ToolWindowPane
    {
        public static HoneyBExploreWindow Instance { get; private set; }
        private readonly HoneyBExploreControl _control;

        public HoneyBExploreWindow() : base(null)
        {
            Caption = "HoneyB Explorer";
            Instance = this;
            _control = new HoneyBExploreControl();
            Content = _control;
        }

        // Called from HoneyBEventListener on UI thread after snapshot is built
        public void OnNewSnapshot(int snapId, string label)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _control.AddSnapshotEntry(snapId, label);
            });
        }

        // Called from HoneyBEventListener on UI thread after locals are available
        public void PopulateExplore(List<LocalsFrame> frames)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _control.PopulateExplore(frames);
            });
        }

        // Called from HoneyBEventListener after watch paths are evaluated
        public void OnWatchEvaluated(List<WatchEvalResult> results)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _control.OnWatchEvaluated(results);
            });
        }
    }

    // ── Intermediate model for locals passed from the event listener ──────────
    public class LocalsFrame
    {
        public string FunctionName { get; set; }
        public string FileName     { get; set; }
        public int    Line         { get; set; }
        public List<LocalVar> Locals { get; set; } = new List<LocalVar>();
    }

    public class LocalVar
    {
        public string Name     { get; set; }
        public string Type     { get; set; }
        public string Value    { get; set; }
        public string DottedPath { get; set; }   // full dotted path relative to frame root
        public List<LocalVar> Children { get; set; } = new List<LocalVar>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main tabbed control
    // ─────────────────────────────────────────────────────────────────────────
    public class HoneyBExploreControl : UserControl
    {
        // ── Tab 1: Chat (replicated from HoneyBChatControl) ──────────────────
        private readonly ListBox _snapshotList;
        private readonly TextBox _questionBox;
        private readonly Button _askButton;
        private readonly StackPanel _chatHistory;
        private readonly ScrollViewer _chatScroll;

        // ── Tab 2: Explore ─────────────────────────────────────────────────
        private readonly TreeView _exploreTree;
        private readonly TextBlock _exploreStatus;

        // ── Tab 3: Watch ─────────────────────────────────────────────────
        private readonly StackPanel _watchPanel;
        private readonly ScrollViewer _watchScroll;
        private readonly TextBlock _watchEmpty;

        // Runtime watch state: path → last known value (for change detection)
        private readonly Dictionary<string, string> _lastWatchValues = new Dictionary<string, string>();
        // Active highlight timers: path → timer
        private readonly Dictionary<string, DispatcherTimer> _highlightTimers = new Dictionary<string, DispatcherTimer>();
        // Watch row borders: path → border for highlight
        private readonly Dictionary<string, Border> _watchRows = new Dictionary<string, Border>();

        private static readonly HttpClient Http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:5678"),
            Timeout = TimeSpan.FromSeconds(120),
        };

        private static readonly SolidColorBrush BgDark    = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
        private static readonly SolidColorBrush BgMid     = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x2d));
        private static readonly SolidColorBrush AccentGold = new SolidColorBrush(Color.FromRgb(0xff, 0xc1, 0x07));
        private static readonly SolidColorBrush TextWhite  = new SolidColorBrush(Colors.White);
        private static readonly SolidColorBrush TextGray   = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xaa));

        public HoneyBExploreControl()
        {
            Background = BgDark;

            var tabs = new TabControl
            {
                Background = BgDark,
                Foreground = TextWhite,
            };

            // ── Tab 1: Chat ───────────────────────────────────────────────────
            var chatContent = BuildChatTab(out _snapshotList, out _questionBox,
                out _askButton, out _chatHistory, out _chatScroll);
            tabs.Items.Add(new TabItem
            {
                Header = "💬 Chat",
                Content = chatContent,
                Background = BgMid,
                Foreground = TextWhite,
            });

            // ── Tab 2: Explore ────────────────────────────────────────────────
            _exploreTree   = new TreeView { Background = BgDark, Foreground = TextWhite };
            _exploreStatus = new TextBlock
            {
                Text = "Waiting for a breakpoint hit…",
                Foreground = TextGray,
                Margin = new Thickness(8),
                FontStyle = FontStyles.Italic,
            };
            var exploreStack = new DockPanel();
            DockPanel.SetDock(_exploreStatus, Dock.Top);
            exploreStack.Children.Add(_exploreStatus);
            exploreStack.Children.Add(new ScrollViewer
            {
                Content = _exploreTree,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });

            tabs.Items.Add(new TabItem
            {
                Header = "🔍 Explore",
                Content = exploreStack,
                Background = BgMid,
                Foreground = TextWhite,
            });

            // ── Tab 3: Watch ──────────────────────────────────────────────────
            _watchPanel = new StackPanel { Margin = new Thickness(4) };
            _watchEmpty  = new TextBlock
            {
                Text = "No pinned paths yet. Use the Explore tab to pin variables.",
                Foreground = TextGray,
                Margin = new Thickness(8),
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
            };
            _watchPanel.Children.Add(_watchEmpty);
            _watchScroll = new ScrollViewer
            {
                Content = _watchPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            tabs.Items.Add(new TabItem
            {
                Header = "📌 Watch",
                Content = _watchScroll,
                Background = BgMid,
                Foreground = TextWhite,
            });

            Content = tabs;
            AddChatMessage("system", "AI Debugger ready. Set a breakpoint and run your program.");

            // Seed the watch tab from the already-loaded WatchStore
            RefreshWatchRows();
        }

        // ── Chat tab builder ──────────────────────────────────────────────────

        private UIElement BuildChatTab(
            out ListBox snapshotList, out TextBox questionBox,
            out Button askButton, out StackPanel chatHistory, out ScrollViewer chatScroll)
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });

            // Snapshot list
            var snapPanel = new DockPanel { Margin = new Thickness(4) };
            var snapLabel = new TextBlock
            {
                Text = "Snapshots",
                FontWeight = FontWeights.Bold,
                Foreground = TextWhite,
                Margin = new Thickness(0, 0, 0, 4),
            };
            DockPanel.SetDock(snapLabel, Dock.Top);
            snapshotList = new ListBox
            {
                Height = 100,
                Background = BgMid,
                Foreground = TextWhite,
            };
            snapPanel.Children.Add(snapLabel);
            snapPanel.Children.Add(snapshotList);
            Grid.SetRow(snapPanel, 0);
            root.Children.Add(snapPanel);

            // Chat history
            chatHistory = new StackPanel { Margin = new Thickness(4) };
            chatScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = chatHistory,
            };
            Grid.SetRow(chatScroll, 1);
            root.Children.Add(chatScroll);

            // Question input
            var inputPanel = new DockPanel { Margin = new Thickness(4) };
            askButton = new Button
            {
                Content = "Ask",
                Width = 50,
                Margin = new Thickness(4, 0, 0, 0),
                Background = AccentGold,
                Foreground = new SolidColorBrush(Colors.Black),
                FontWeight = FontWeights.Bold,
            };
            askButton.Click += OnAskClicked;
            DockPanel.SetDock(askButton, Dock.Right);

            questionBox = new TextBox
            {
                AcceptsReturn = false,
                VerticalAlignment = VerticalAlignment.Center,
                Background = BgMid,
                Foreground = TextWhite,
                CaretBrush = TextWhite,
            };
            questionBox.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) OnAskClicked(s, e);
            };

            inputPanel.Children.Add(askButton);
            inputPanel.Children.Add(questionBox);
            Grid.SetRow(inputPanel, 2);
            root.Children.Add(inputPanel);

            return root;
        }

        // ── Public API called from HoneyBExploreWindow ────────────────────────

        public void AddSnapshotEntry(int snapId, string label)
        {
            var item = new ListBoxItem
            {
                Content = $"#{snapId} — {label}",
                Tag = snapId,
                Foreground = TextWhite,
            };
            _snapshotList.Items.Add(item);
            _snapshotList.SelectedItem = item;
            AddChatMessage("system", $"📸 Snapshot #{snapId} captured: {label}");
        }

        /// <summary>
        /// Populate the Explore TreeView with locals from the current pause.
        /// </summary>
        public void PopulateExplore(List<LocalsFrame> frames)
        {
            _exploreTree.Items.Clear();

            if (frames == null || frames.Count == 0)
            {
                _exploreStatus.Text = "No locals available for this frame.";
                return;
            }

            _exploreStatus.Text = $"Paused at {frames[0].FunctionName} ({frames[0].FileName}:{frames[0].Line})";

            foreach (var frame in frames)
            {
                var frameItem = new TreeViewItem
                {
                    Header = MakeFrameHeader(frame.FunctionName),
                    IsExpanded = true,
                    Background = BgMid,
                };

                foreach (var local in frame.Locals)
                {
                    frameItem.Items.Add(MakeLocalItem(local, frame.FunctionName));
                }

                _exploreTree.Items.Add(frameItem);
            }
        }

        /// <summary>
        /// Update the Watch tab with freshly evaluated values.
        /// </summary>
        public void OnWatchEvaluated(List<WatchEvalResult> results)
        {
            if (results == null) return;

            foreach (var r in results)
            {
                // Update existing row if present
                if (_watchRows.TryGetValue(r.Path, out var border))
                {
                    UpdateWatchRow(border, r);
                }
            }

            // Rebuild rows for any newly-pinned paths that don't have a row yet
            foreach (var r in results)
            {
                if (!_watchRows.ContainsKey(r.Path))
                    RefreshWatchRows();
            }
        }

        // ── Explore tree helpers ──────────────────────────────────────────────

        private UIElement MakeFrameHeader(string functionName)
        {
            return new TextBlock
            {
                Text = $"▶ {functionName}",
                FontWeight = FontWeights.Bold,
                Foreground = AccentGold,
            };
        }

        private TreeViewItem MakeLocalItem(LocalVar local, string seenIn)
        {
            var header = MakeVarHeader(local, seenIn);
            var item = new TreeViewItem { Header = header, Background = BgDark };

            foreach (var child in local.Children)
            {
                item.Items.Add(MakeLocalItem(child, seenIn));
            }

            return item;
        }

        private UIElement MakeVarHeader(LocalVar local, string seenIn)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            // Variable label
            var label = new TextBlock
            {
                Text = $"{local.Type} {local.Name} = {local.Value}",
                Foreground = TextWhite,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            panel.Children.Add(label);

            // 📌 Pin button (hidden until hover)
            var pinBtn = new Button
            {
                Content = "📌",
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(2),
                Margin = new Thickness(2, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Pin \"{local.DottedPath}\" to Watch list",
            };

            // Capture for closure
            string path   = local.DottedPath;
            string lbl    = local.Name;
            string seen   = seenIn;

            pinBtn.Click += (s, e) =>
            {
                bool added = WatchStore.Instance.AddPath(path, lbl, seen);
                if (added) RefreshWatchRows();
            };

            // Show/hide on hover
            panel.MouseEnter += (s, e) => pinBtn.Visibility = Visibility.Visible;
            panel.MouseLeave += (s, e) => pinBtn.Visibility = Visibility.Collapsed;

            panel.Children.Add(pinBtn);
            return panel;
        }

        // ── Watch tab helpers ─────────────────────────────────────────────────

        private void RefreshWatchRows()
        {
            _watchPanel.Children.Clear();
            _watchRows.Clear();

            var paths = WatchStore.Instance.Paths;
            if (paths.Count == 0)
            {
                _watchPanel.Children.Add(_watchEmpty);
                return;
            }

            // Column header
            _watchPanel.Children.Add(MakeWatchHeader());

            foreach (var wp in paths)
            {
                string lastVal = _lastWatchValues.TryGetValue(wp.Path, out var v) ? v : "—";
                var row = MakeWatchRow(wp, lastVal);
                _watchRows[wp.Path] = row;
                _watchPanel.Children.Add(row);
            }
        }

        private UIElement MakeWatchHeader()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            void AddHeader(string text, int col)
            {
                var tb = new TextBlock
                {
                    Text = text,
                    FontWeight = FontWeights.Bold,
                    Foreground = AccentGold,
                    Padding = new Thickness(4, 2, 4, 2),
                };
                Grid.SetColumn(tb, col);
                grid.Children.Add(tb);
            }

            AddHeader("Path", 0);
            AddHeader("Value", 1);
            AddHeader("SeenIn", 2);
            AddHeader("", 3);

            return new Border
            {
                Background = BgMid,
                BorderBrush = AccentGold,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = grid,
            };
        }

        private Border MakeWatchRow(WatchedPath wp, string currentValue)
        {
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });

            void AddCell(string text, int col, bool accent = false)
            {
                var tb = new TextBlock
                {
                    Text = text,
                    Foreground = accent ? AccentGold : TextWhite,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(4, 4, 4, 4),
                };
                Grid.SetColumn(tb, col);
                grid.Children.Add(tb);
            }

            AddCell(wp.Path, 0, accent: true);
            AddCell(currentValue, 1);
            AddCell(wp.SeenIn, 2);

            // Remove button
            var removeBtn = new Button
            {
                Content = "✕",
                Padding = new Thickness(4, 2, 4, 2),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0x55, 0x55)),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Remove \"{wp.Path}\" from Watch list",
            };
            string capPath = wp.Path;
            removeBtn.Click += (s, e) =>
            {
                WatchStore.Instance.RemovePath(capPath);
                RefreshWatchRows();
            };
            Grid.SetColumn(removeBtn, 3);
            grid.Children.Add(removeBtn);

            var border = new Border
            {
                Background = BgMid,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2),
                Child = grid,
                Tag = wp.Path,
            };
            return border;
        }

        private void UpdateWatchRow(Border border, WatchEvalResult result)
        {
            if (border.Child is Grid grid)
            {
                // Find the value TextBlock (column 1)
                foreach (UIElement child in grid.Children)
                {
                    if (child is TextBlock tb && Grid.GetColumn(tb) == 1)
                    {
                        string oldValue = tb.Text;
                        string newValue = result.Value ?? "— (not in scope) —";
                        tb.Text = newValue;

                        bool changed = !string.Equals(oldValue, newValue, StringComparison.Ordinal)
                                       && oldValue != "—";
                        if (changed) FlashRow(border, result.Path);
                        break;
                    }
                }
            }

            if (result.Value != null)
                _lastWatchValues[result.Path] = result.Value;
        }

        private void FlashRow(Border border, string path)
        {
            // Cancel any existing flash timer
            if (_highlightTimers.TryGetValue(path, out var existing))
                existing.Stop();

            border.Background = new SolidColorBrush(Color.FromArgb(0xaa, 0xff, 0xd7, 0x00));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                border.Background = BgMid;
                _highlightTimers.Remove(path);
            };
            _highlightTimers[path] = timer;
            timer.Start();
        }

        // ── Chat helpers (same logic as HoneyBChatControl) ────────────────────

        private async void OnAskClicked(object sender, RoutedEventArgs e)
        {
            var question = _questionBox.Text?.Trim();
            if (string.IsNullOrEmpty(question)) return;

            int? snapId = null;
            if (_snapshotList.SelectedItem is ListBoxItem item)
                snapId = (int)item.Tag;

            _questionBox.Clear();
            _askButton.IsEnabled = false;
            AddChatMessage("user", question);
            AddChatMessage("thinking", "Thinking…");

            try
            {
                var payload = JsonConvert.SerializeObject(new { question, snapshot_id = snapId });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await Http.PostAsync("/query", content);
                var body = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<dynamic>(body);
                string answer = result?.answer ?? "No answer returned.";

                RemoveThinking();
                AddChatMessage("ai", answer);
            }
            catch (Exception ex)
            {
                RemoveThinking();
                AddChatMessage("error", $"Error: {ex.Message}");
            }
            finally
            {
                _askButton.IsEnabled = true;
            }
        }

        private void AddChatMessage(string role, string text)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(4),
                Background = role switch
                {
                    "user"     => new SolidColorBrush(Color.FromRgb(0x1e, 0x3a, 0x5f)),
                    "ai"       => new SolidColorBrush(Color.FromRgb(0x1e, 0x4a, 0x2e)),
                    "thinking" => new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x2a)),
                    "error"    => new SolidColorBrush(Color.FromRgb(0x5f, 0x1e, 0x1e)),
                    _          => BgMid,
                },
                Tag = role,
            };

            var labelBlock = new TextBlock
            {
                Text = role switch
                {
                    "user"     => "You",
                    "ai"       => "AI",
                    "thinking" => "…",
                    "error"    => "Error",
                    _          => "System",
                },
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4),
            };

            var msgBlock = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
            };

            var stack = new StackPanel();
            stack.Children.Add(labelBlock);
            stack.Children.Add(msgBlock);
            border.Child = stack;

            _chatHistory.Children.Add(border);
            _chatScroll.ScrollToBottom();
        }

        private void RemoveThinking()
        {
            for (int i = _chatHistory.Children.Count - 1; i >= 0; i--)
            {
                if (_chatHistory.Children[i] is Border b && (string)b.Tag == "thinking")
                {
                    _chatHistory.Children.RemoveAt(i);
                    break;
                }
            }
        }
    }
}
