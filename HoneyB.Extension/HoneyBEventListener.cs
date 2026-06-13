using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using EnvDTE90;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

namespace HoneyB
{
    /// <summary>
    /// Listens for debugger pause events (breakpoints, steps, exceptions).
    /// When execution pauses, reads all locals via DTE and ships them
    /// to the Python backend as a snapshot.
    /// </summary>
    public class HoneyBEventListener
    {
        private readonly AsyncPackage _package;
        private DTE2 _dte;
        private DebuggerEvents _debuggerEvents;
        private static readonly HttpClient Http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:5678"),
            Timeout = TimeSpan.FromSeconds(10),
        };

        public HoneyBEventListener(AsyncPackage package)
        {
            _package = package;
        }

        public void Start()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _dte = (DTE2)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(DTE));
            if (_dte == null) return;

            _debuggerEvents = _dte.Events.DebuggerEvents;
            _debuggerEvents.OnEnterBreakMode += OnEnterBreakMode;
            _debuggerEvents.OnEnterRunMode += OnEnterRunMode;
            _debuggerEvents.OnEnterDesignMode += OnEnterDesignMode;
        }

        public void Stop()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_debuggerEvents != null)
            {
                _debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
                _debuggerEvents.OnEnterRunMode -= OnEnterRunMode;
                _debuggerEvents.OnEnterDesignMode -= OnEnterDesignMode;
            }
        }

        private static int _nextSnapId = 1;

        private static readonly object _fileLock = new object();

        private void OnEnterRunMode(dbgEventReason reason)
        {
            lock (_fileLock)
            {
                try
                {
                    var tempFile = HoneyBUtils.GetTimelineFilePath();
                    if (System.IO.File.Exists(tempFile))
                        System.IO.File.Delete(tempFile);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AiDebugger] Timeline reset failed: {ex.Message}");
                }
            }
            _nextSnapId = 1;

            _ = SendEmptyEventAsync("Session Started", "session_start");
        }

        private void OnEnterDesignMode(dbgEventReason reason)
        {
            _ = SendEmptyEventAsync("Session Ended", "session_end");
        }

        private async Task SendEmptyEventAsync(string label, string eventKind)
        {
            var snapshot = new SnapshotPayload
            {
                Label = label,
                EventKind = eventKind,
                ThreadName = "",
                Frames = new List<FramePayload>(),
                SourceContext = null
            };
            await SendSnapshotAsync(snapshot);
        }

        private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var snapshot = BuildSnapshot(reason);
                if (snapshot != null)
                    _ = SendSnapshotAsync(snapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiDebugger] Snapshot error: {ex.Message}");
            }
        }

        private SnapshotPayload BuildSnapshot(dbgEventReason reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var debugger = _dte.Debugger as Debugger2;
            if (debugger == null) return null;

            string threadName = "Main Thread";
            try
            {
                threadName = debugger.CurrentThread.Name;
                if (string.IsNullOrEmpty(threadName)) threadName = $"Thread #{debugger.CurrentThread.ID}";
            }
            catch { }

            var frames = new List<FramePayload>();

            // Walk all stack frames
            foreach (dynamic frame in debugger.CurrentThread.StackFrames)
            {
                var locals = new List<VariablePayload>();

                try
                {
                    foreach (Expression local in frame.Locals)
                    {
                        var varPayload = new VariablePayload
                        {
                            Name = local.Name,
                            Type = local.Type,
                            Value = local.Value,
                            Children = new List<VariablePayload>()
                        };

                        // One level of children (fields of objects)
                        if (local.DataMembers != null)
                        {
                            foreach (Expression member in local.DataMembers)
                            {
                                varPayload.Children.Add(new VariablePayload
                                {
                                    Name = member.Name,
                                    Type = member.Type,
                                    Value = member.Value
                                });
                            }
                        }
                        locals.Add(varPayload);
                    }
                }
                catch { /* some frames have no accessible locals */ }

                string frameFunctionName = "?";
                string frameFileName = "?";
                int frameLineNumber = 0;
                try { frameFunctionName = (string)frame.FunctionName; } catch { }
                try { frameFileName = (string)frame.FileName; } catch { }
                try { frameLineNumber = (int)frame.LineNumber; } catch { }

                frames.Add(new FramePayload
                {
                    Function = frameFunctionName,
                    File = frameFileName,
                    Line = frameLineNumber,
                    Locals = locals,
                });

                // Cap at 5 frames to keep context manageable
                if (frames.Count >= 5) break;
            }

            // Grab a few lines of source around the current line
            string sourceContext = null;
            try
            {
                var currentDoc = _dte.ActiveDocument;
                if (currentDoc != null)
                {
                    var sel = (TextSelection)currentDoc.Selection;
                    int currentLine = sel.ActivePoint.Line;
                    var buf = ((TextDocument)currentDoc.Object()).StartPoint
                        .CreateEditPoint();
                    // Read lines around breakpoint
                    int startLine = Math.Max(1, currentLine - 3);
                    var ep = buf.CreateEditPoint();
                    ep.MoveToLineAndOffset(startLine, 1);
                    sourceContext = ep.GetLines(startLine, Math.Min(currentLine + 3,
                        ((TextDocument)currentDoc.Object()).EndPoint.Line));
                }
            }
            catch { /* source context is optional */ }

            string label = reason switch
            {
                dbgEventReason.dbgEventReasonBreakpoint => "Breakpoint hit",
                dbgEventReason.dbgEventReasonStep => "Step",
                dbgEventReason.dbgEventReasonExceptionThrown => "Exception thrown",
                dbgEventReason.dbgEventReasonExceptionNotHandled => "Unhandled exception",
                _ => reason.ToString(),
            };

            string eventKind = reason switch
            {
                dbgEventReason.dbgEventReasonBreakpoint => "breakpoint",
                dbgEventReason.dbgEventReasonStep => "step",
                dbgEventReason.dbgEventReasonExceptionThrown => "exception",
                dbgEventReason.dbgEventReasonExceptionNotHandled => "exception",
                _ => "pause",
            };

            if (frames.Count > 0)
                label += $" at {frames[0].File}:{frames[0].Line}";

            return new SnapshotPayload
            {
                Label = label,
                EventKind = eventKind,
                ThreadName = threadName,
                Frames = frames,
                SourceContext = sourceContext,
            };
        }

        private async Task SendSnapshotAsync(SnapshotPayload snapshot)
        {
            try
            {
                var entry = new
                {
                    id = _nextSnapId++,
                    label = snapshot.Label,
                    event_kind = snapshot.EventKind,
                    thread_name = snapshot.ThreadName,
                    frames = snapshot.Frames,
                    changed_vars = new List<string>()
                };

                string jsonEntry = JsonConvert.SerializeObject(entry, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                });

                // Write timeline to local file
                lock (_fileLock)
                {
                    HoneyBUtils.AppendTimelineEntry(entry);
                }

                // Notify local windows immediately
                HoneyBChatWindow.Instance?.OnNewSnapshot(entry.id, snapshot.Label);
                HoneyBTimelineWindow.Instance?.OnNewTimelineEntry(jsonEntry);

                // Best-effort send to backend if running, but we don't rely on its response
                var content = new StringContent(JsonConvert.SerializeObject(snapshot, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                }), Encoding.UTF8, "application/json");
                _ = Http.PostAsync("/snapshot", content); 
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiDebugger] Send failed: {ex.Message}");
            }
        }
    }

    public static class HoneyBUtils
    {
        public static string GetTimelineFilePath()
        {
            string configuredPath = null;
            string solutionDir = null;
            string solutionName = "default";

            try
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var dte = (EnvDTE.DTE)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE));
                    if (dte != null && dte.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                    {
                        solutionDir = System.IO.Path.GetDirectoryName(dte.Solution.FullName);
                        solutionName = System.IO.Path.GetFileNameWithoutExtension(dte.Solution.FullName);
                        
                        // Check for project-level config
                        string configPath = System.IO.Path.Combine(solutionDir, "honeyb.json");
                        if (System.IO.File.Exists(configPath))
                        {
                            try
                            {
                                string configJson = System.IO.File.ReadAllText(configPath);
                                var config = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(configJson);
                                if (config != null && config.timelinePath != null)
                                {
                                    string customPath = (string)config.timelinePath;
                                    if (!string.IsNullOrWhiteSpace(customPath))
                                    {
                                        if (!System.IO.Path.IsPathRooted(customPath))
                                            customPath = System.IO.Path.Combine(solutionDir, customPath);
                                        configuredPath = customPath;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                });
            }
            catch { }

            if (!string.IsNullOrEmpty(configuredPath))
                return configuredPath;

            return GetDefaultTimelineFilePath(solutionName);
        }

        public static void AppendTimelineEntry(object entry)
        {
            WriteTimelineEntry(entry, GetTimelineFilePath());
        }

        private static void WriteTimelineEntry(object entry, string timelinePath)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(timelinePath);
                if (!string.IsNullOrEmpty(dir))
                    System.IO.Directory.CreateDirectory(dir);

                var entries = new List<dynamic>();
                if (System.IO.File.Exists(timelinePath))
                {
                    try
                    {
                        entries = JsonConvert.DeserializeObject<List<dynamic>>(System.IO.File.ReadAllText(timelinePath)) ?? new List<dynamic>();
                    }
                    catch { }
                }

                entries.Add(entry);
                System.IO.File.WriteAllText(timelinePath, JsonConvert.SerializeObject(entries, Formatting.Indented, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                }));
            }
            catch (UnauthorizedAccessException ex)
            {
                string fallbackPath = GetDefaultTimelineFilePath("default");
                if (!string.Equals(timelinePath, fallbackPath, StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine($"[AiDebugger] Timeline path denied ({timelinePath}); falling back to {fallbackPath}: {ex.Message}");
                    WriteTimelineEntry(entry, fallbackPath);
                    return;
                }

                throw;
            }
        }

        private static string GetDefaultTimelineFilePath(string solutionName)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = System.IO.Path.GetTempPath();

            string safeSolutionName = MakeSafeFileName(solutionName);
            return System.IO.Path.Combine(localAppData, "HoneyB", safeSolutionName, "honeyb_timeline.json");
        }

        private static string MakeSafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "default";

            foreach (char invalidChar in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(invalidChar, '_');

            return string.IsNullOrWhiteSpace(name) ? "default" : name;
        }
    }

    // ── Payload models ────────────────────────────────────────────────────────

    public class SnapshotPayload
    {
        public string Label { get; set; }
        [JsonProperty("event_kind")]
        public string EventKind { get; set; }
        [JsonProperty("thread_name")]
        public string ThreadName { get; set; }
        public List<FramePayload> Frames { get; set; }
        [JsonProperty("source_context")]
        public string SourceContext { get; set; }
    }

    public class FramePayload
    {
        public string Function { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public List<VariablePayload> Locals { get; set; }
    }

    public class VariablePayload
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
        public List<VariablePayload> Children { get; set; }
    }
}
