using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

namespace HoneyB
{
    /// <summary>
    /// One entry in the .honeybwatch whitelist.
    /// Committable to source control — only stores dotted paths, not values.
    /// </summary>
    public class WatchedPath
    {
        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary>
        /// Function name where the variable was visible when it was pinned.
        /// </summary>
        [JsonProperty("seenIn")]
        public string SeenIn { get; set; }
    }

    /// <summary>
    /// Wrapper for the on-disk .honeybwatch file.
    /// </summary>
    internal class WatchFile
    {
        [JsonProperty("watchedPaths")]
        public List<WatchedPath> WatchedPaths { get; set; } = new List<WatchedPath>();
    }

    /// <summary>
    /// Thread-safe singleton that manages the pinned-path whitelist.
    /// Call Load() once on extension startup (UI thread).
    /// AddPath / RemovePath are safe from any thread.
    /// </summary>
    public class WatchStore
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        private static WatchStore _instance;
        public static WatchStore Instance => _instance ?? (_instance = new WatchStore());

        // ── State ──────────────────────────────────────────────────────────────
        private readonly object _lock = new object();
        private readonly List<WatchedPath> _paths = new List<WatchedPath>();
        private string _filePath;   // full path to .honeybwatch

        /// <summary>
        /// Read-only snapshot of the current whitelist (safe to enumerate).
        /// </summary>
        public IReadOnlyList<WatchedPath> Paths
        {
            get { lock (_lock) { return _paths.ToList(); } }
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// Load (or create) the .honeybwatch file from the solution root.
        /// Must be called on the UI thread so we can resolve the solution path.
        /// </summary>
        public void Load(string solutionDirectory)
        {
            if (string.IsNullOrWhiteSpace(solutionDirectory)) return;

            _filePath = System.IO.Path.Combine(solutionDirectory, ".honeybwatch");

            lock (_lock)
            {
                _paths.Clear();
                if (File.Exists(_filePath))
                {
                    try
                    {
                        var json = File.ReadAllText(_filePath);
                        var wf = JsonConvert.DeserializeObject<WatchFile>(json);
                        if (wf?.WatchedPaths != null)
                            _paths.AddRange(wf.WatchedPaths);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[HoneyB] WatchStore.Load failed: {ex.Message}");
                    }
                }
            }
        }

        // ── Mutation ───────────────────────────────────────────────────────────

        /// <summary>
        /// Add a dotted path to the whitelist (no-op if already present).
        /// </summary>
        public bool AddPath(string dottedPath, string label, string seenIn)
        {
            if (string.IsNullOrWhiteSpace(dottedPath)) return false;

            lock (_lock)
            {
                if (_paths.Any(p => p.Path == dottedPath)) return false;
                _paths.Add(new WatchedPath
                {
                    Path  = dottedPath,
                    Label = string.IsNullOrWhiteSpace(label) ? dottedPath : label,
                    SeenIn = seenIn ?? ""
                });
                Save();
                return true;
            }
        }

        /// <summary>
        /// Remove a path from the whitelist.
        /// </summary>
        public bool RemovePath(string dottedPath)
        {
            lock (_lock)
            {
                int removed = _paths.RemoveAll(p => p.Path == dottedPath);
                if (removed > 0) { Save(); return true; }
                return false;
            }
        }

        // ── Persistence ────────────────────────────────────────────────────────

        private void Save()
        {
            // Already inside _lock when called
            if (string.IsNullOrWhiteSpace(_filePath)) return;
            try
            {
                var wf = new WatchFile { WatchedPaths = new List<WatchedPath>(_paths) };
                File.WriteAllText(_filePath, JsonConvert.SerializeObject(wf, Formatting.Indented));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HoneyB] WatchStore.Save failed: {ex.Message}");
            }
        }
    }
}
