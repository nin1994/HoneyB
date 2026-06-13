using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

namespace HoneyB
{
    /// <summary>
    /// Tool window that shows the chronological timeline of debugger events.
    /// Appears as a dockable panel inside Visual Studio.
    /// </summary>
    [Guid("d1e2f3a4-b5c6-7890-defa-bc1234567890")]
    public class HoneyBTimelineWindow : ToolWindowPane
    {
        public static HoneyBTimelineWindow Instance { get; private set; }
        private HoneyBTimelineControl _control;

        public HoneyBTimelineWindow() : base(null)
        {
            Caption = "HoneyB Timeline";
            Instance = this;
            _control = new HoneyBTimelineControl();
            Content = _control;
        }

        public void OnNewTimelineEntry(string jsonEntry)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _control.AddTimelineEntry(jsonEntry);
            });
        }
    }

    /// <summary>
    /// WPF control for the timeline. Contains a horizontal rail of dots
    /// and a details panel showing the object tree for the selected node.
    /// </summary>
    public class HoneyBTimelineControl : UserControl
    {
        private readonly Canvas _rail;
        private readonly ScrollViewer _railScroll;
        private readonly StackPanel _detailPanel;
        private readonly TreeView _objectTree;
        
        private readonly List<dynamic> _entries = new List<dynamic>();
        private readonly int _dotSize = 20;
        private readonly int _spacing = 40;

        public HoneyBTimelineControl()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) }); // Rail
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Detail

            // ── Timeline Rail ───────────────────────────────────────────────
            _rail = new Canvas { Height = 40, VerticalAlignment = VerticalAlignment.Center };
            _railScroll = new ScrollViewer 
            { 
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _rail,
                Margin = new Thickness(10)
            };
            Grid.SetRow(_railScroll, 0);
            root.Children.Add(_railScroll);

            // ── Detail & Tree Panel ─────────────────────────────────────────
            var detailGrid = new Grid();
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Info
            detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Tree
            Grid.SetRow(detailGrid, 1);
            root.Children.Add(detailGrid);

            _detailPanel = new StackPanel { Margin = new Thickness(10) };
            Grid.SetColumn(_detailPanel, 0);
            detailGrid.Children.Add(_detailPanel);

            _objectTree = new TreeView { Margin = new Thickness(10), Background = Brushes.Transparent };
            Grid.SetColumn(_objectTree, 1);
            detailGrid.Children.Add(_objectTree);

            Content = root;
            
            // Load initial full timeline from backend
            _ = LoadTimelineAsync();
        }
        
        private async System.Threading.Tasks.Task LoadTimelineAsync()
        {
            try
            {
                using (var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5678"), Timeout = TimeSpan.FromSeconds(5) })
                {
                    var response = await http.GetAsync("/timeline");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var entries = JsonConvert.DeserializeObject<List<dynamic>>(json);
                        foreach (var e in entries)
                        {
                            AddTimelineEntry(JsonConvert.SerializeObject(e), render: false);
                        }
                        RenderRail();
                    }
                }
            }
            catch { /* Backend not running yet */ }
        }

        public void AddTimelineEntry(string jsonEntry, bool render = true)
        {
            var entry = JsonConvert.DeserializeObject<dynamic>(jsonEntry);
            _entries.Add(entry);
            if (render) RenderRail();
        }

        private void RenderRail()
        {
            _rail.Children.Clear();
            _rail.Width = Math.Max(400, _entries.Count * _spacing + _spacing);

            // Draw connecting line
            var line = new Line
            {
                X1 = _spacing / 2,
                Y1 = _dotSize / 2,
                X2 = _rail.Width - _spacing / 2,
                Y2 = _dotSize / 2,
                Stroke = Brushes.Gray,
                StrokeThickness = 2
            };
            _rail.Children.Add(line);

            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                string kind = (string)entry.event_kind;

                Brush fill = Brushes.Gray; // step
                if (kind == "breakpoint") fill = Brushes.Gold;
                else if (kind == "exception") fill = Brushes.Tomato;
                else if (kind == "session_start" || kind == "session_end") fill = Brushes.DodgerBlue;

                var dot = new Ellipse
                {
                    Width = _dotSize,
                    Height = _dotSize,
                    Fill = fill,
                    Stroke = Brushes.White,
                    StrokeThickness = 2,
                    ToolTip = (string)entry.label,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                Canvas.SetLeft(dot, i * _spacing + (_spacing - _dotSize) / 2);
                Canvas.SetTop(dot, 0);
                
                int index = i;
                dot.MouseLeftButtonDown += (s, e) => ShowDetail(index);
                
                _rail.Children.Add(dot);
            }
            
            _railScroll.ScrollToRightEnd();
        }

        private void ShowDetail(int index)
        {
            var entry = _entries[index];
            _detailPanel.Children.Clear();

            _detailPanel.Children.Add(new TextBlock 
            { 
                Text = $"#{(int)entry.id} - {(string)entry.label}", 
                FontWeight = FontWeights.Bold, 
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            _detailPanel.Children.Add(new TextBlock { Text = $"Event: {(string)entry.event_kind}" });
            _detailPanel.Children.Add(new TextBlock { Text = $"Thread: {(string)entry.thread_name}" });

            var frames = entry.frames;
            if (frames != null && frames.Count > 0)
            {
                _detailPanel.Children.Add(new TextBlock { Text = "\nCall Stack:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 5) });
                foreach (var f in frames)
                {
                    _detailPanel.Children.Add(new TextBlock { Text = $"  ▶ {f.function} ({f.file}:{f.line})" });
                }
            }

            // Populate Object Tree
            _objectTree.Items.Clear();
            if (frames != null && frames.Count > 0)
            {
                var topFrame = frames[0];
                var locals = topFrame.locals;
                
                var changedVars = new HashSet<string>();
                if (entry.changed_vars != null)
                {
                    foreach (string cv in entry.changed_vars) changedVars.Add(cv);
                }

                if (locals != null)
                {
                    foreach (var local in locals)
                    {
                        var item = CreateTreeItem(local, topFrame.function.ToString(), changedVars);
                        _objectTree.Items.Add(item);
                    }
                }
            }
        }
        
        private TreeViewItem CreateTreeItem(dynamic variable, string prefix, HashSet<string> changedVars)
        {
            string name = (string)variable.name;
            string key = $"{prefix}.{name}";
            bool isChanged = changedVars.Contains(key);

            var header = new TextBlock { Text = $"{variable.type} {name} = {variable.value}" };
            if (isChanged)
            {
                header.Foreground = Brushes.Tomato;
                header.FontWeight = FontWeights.Bold;
                header.Text += " ✏️ (changed)";
            }

            var item = new TreeViewItem { Header = header };
            
            if (variable.children != null)
            {
                foreach (var child in variable.children)
                {
                    item.Items.Add(CreateTreeItem(child, key, changedVars));
                }
            }
            return item;
        }
    }
}
