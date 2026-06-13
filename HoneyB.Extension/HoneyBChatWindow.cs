using System;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

namespace HoneyB
{
    /// <summary>
    /// Tool window that shows snapshot history and a chat interface.
    /// Appears as a dockable panel inside Visual Studio.
    /// </summary>
    [Guid("b1c2d3e4-f5a6-7890-bcde-fa1234567890")]
    public class HoneyBChatWindow : ToolWindowPane
    {
        public static HoneyBChatWindow Instance { get; private set; }
        private HoneyBChatControl _control;

        public HoneyBChatWindow() : base(null)
        {
            Caption = "HoneyB";
            Instance = this;
            _control = new HoneyBChatControl();
            Content = _control;
        }

        public void OnNewSnapshot(int snapId, string label)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _control.AddSnapshotEntry(snapId, label);
            });
        }
    }

    /// <summary>
    /// WPF control that lives inside the tool window.
    /// Shows snapshot list + chat input/output.
    /// </summary>
    public class HoneyBChatControl : UserControl
    {
        private readonly ListBox _snapshotList;
        private readonly TextBox _questionBox;
        private readonly Button _askButton;
        private readonly StackPanel _chatHistory;
        private readonly ScrollViewer _chatScroll;
        private static readonly HttpClient Http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:5678"),
            Timeout = TimeSpan.FromSeconds(120),
        };

        public HoneyBChatControl()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) });

            // ── Snapshot list ────────────────────────────────────────────────
            var snapPanel = new DockPanel { Margin = new Thickness(4) };
            var snapLabel = new TextBlock
            {
                Text = "Snapshots",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4),
            };
            DockPanel.SetDock(snapLabel, Dock.Top);
            _snapshotList = new ListBox { Height = 100 };
            _snapshotList.SelectionChanged += OnSnapshotSelected;
            snapPanel.Children.Add(snapLabel);
            snapPanel.Children.Add(_snapshotList);
            Grid.SetRow(snapPanel, 0);
            root.Children.Add(snapPanel);

            // ── Chat history ─────────────────────────────────────────────────
            _chatHistory = new StackPanel { Margin = new Thickness(4) };
            _chatScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _chatHistory,
            };
            Grid.SetRow(_chatScroll, 1);
            root.Children.Add(_chatScroll);

            // ── Question input ────────────────────────────────────────────────
            var inputPanel = new DockPanel { Margin = new Thickness(4) };
            _askButton = new Button
            {
                Content = "Ask",
                Width = 50,
                Margin = new Thickness(4, 0, 0, 0),
            };
            _askButton.Click += OnAskClicked;
            DockPanel.SetDock(_askButton, Dock.Right);

            _questionBox = new TextBox
            {
                AcceptsReturn = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _questionBox.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) OnAskClicked(s, e);
            };

            inputPanel.Children.Add(_askButton);
            inputPanel.Children.Add(_questionBox);
            Grid.SetRow(inputPanel, 2);
            root.Children.Add(inputPanel);

            Content = root;
            AddChatMessage("system", "AI Debugger ready. Set a breakpoint and run your program.");
        }

        public void AddSnapshotEntry(int snapId, string label)
        {
            var item = new ListBoxItem
            {
                Content = $"#{snapId} — {label}",
                Tag = snapId,
            };
            _snapshotList.Items.Add(item);
            _snapshotList.SelectedItem = item;   // auto-select latest
            AddChatMessage("system", $"📸 Snapshot #{snapId} captured: {label}");
        }

        private void OnSnapshotSelected(object sender, SelectionChangedEventArgs e)
        {
            // Selection change just updates which snapshot future queries target.
            // Nothing to do here unless we want to show variable state inline.
        }

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
            AddChatMessage("thinking", "Thinking...");

            try
            {
                var payload = JsonConvert.SerializeObject(new
                {
                    question,
                    snapshot_id = snapId,
                });
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
                    "user" => new SolidColorBrush(Color.FromRgb(0x1e, 0x3a, 0x5f)),
                    "ai" => new SolidColorBrush(Color.FromRgb(0x1e, 0x4a, 0x2e)),
                    "thinking" => new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x2a)),
                    "error" => new SolidColorBrush(Color.FromRgb(0x5f, 0x1e, 0x1e)),
                    _ => new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a)),
                },
                Tag = role,
            };

            var label = new TextBlock
            {
                Text = role switch
                {
                    "user" => "You",
                    "ai" => "AI",
                    "thinking" => "...",
                    "error" => "Error",
                    _ => "System",
                },
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 4),
            };

            var message = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
            };

            var stack = new StackPanel();
            stack.Children.Add(label);
            stack.Children.Add(message);
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
