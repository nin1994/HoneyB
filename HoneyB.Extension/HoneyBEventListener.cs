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
        }

        public void Stop()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_debuggerEvents != null)
                _debuggerEvents.OnEnterBreakMode -= OnEnterBreakMode;
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

            var frames = new List<FramePayload>();

            // Walk all stack frames
            foreach (dynamic frame in debugger.CurrentThread.StackFrames)
            {
                var locals = new List<VariablePayload>();

                try
                {
                    foreach (Expression local in frame.Locals)
                    {
                        locals.Add(new VariablePayload
                        {
                            Name = local.Name,
                            Type = local.Type,
                            Value = local.Value,
                        });

                        // One level of children (fields of objects)
                        if (local.DataMembers != null)
                        {
                            foreach (Expression member in local.DataMembers)
                            {
                                locals.Add(new VariablePayload
                                {
                                    Name = $"{local.Name}.{member.Name}",
                                    Type = member.Type,
                                    Value = member.Value,
                                });
                            }
                        }
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

            if (frames.Count > 0)
                label += $" at {frames[0].File}:{frames[0].Line}";

            return new SnapshotPayload
            {
                Label = label,
                Frames = frames,
                SourceContext = sourceContext,
            };
        }

        private async Task SendSnapshotAsync(SnapshotPayload snapshot)
        {
            try
            {
                var json = JsonConvert.SerializeObject(snapshot, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization
                        .CamelCasePropertyNamesContractResolver()
                });
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await Http.PostAsync("/snapshot", content);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var result = JsonConvert.DeserializeObject<dynamic>(body);
                    int snapId = (int)result.snapshot_id;

                    // Notify the chat window a new snapshot arrived
                    HoneyBChatWindow.Instance?.OnNewSnapshot(snapId, snapshot.Label);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AiDebugger] Send failed: {ex.Message}");
            }
        }
    }

    // ── Payload models ────────────────────────────────────────────────────────

    public class SnapshotPayload
    {
        public string Label { get; set; }
        public List<FramePayload> Frames { get; set; }
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
    }
}
