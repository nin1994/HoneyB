using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace HoneyB
{
    /// <summary>
    /// Main package. Registers the tool windows and hooks into VS debugger events.
    /// Auto-shows the Explorer window when the package loads (triggered by debug start).
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideToolWindow(typeof(HoneyBChatWindow))]
    [ProvideToolWindow(typeof(HoneyBTimelineWindow))]
    [ProvideToolWindow(typeof(HoneyBExploreWindow))]
    // NOTE: No [ProvideMenuResource] — menus are not needed; windows auto-show on load.
    [ProvideAutoLoad(UIContextGuids80.Debugging, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class HoneyBPackage : AsyncPackage
    {
        public const string PackageGuidString = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        private HoneyBEventListener _listener;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Load the .honeybwatch whitelist from the solution root
            try
            {
                var dte = (DTE)GetGlobalService(typeof(DTE));
                if (dte?.Solution != null && !string.IsNullOrEmpty(dte.Solution.FullName))
                {
                    string solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                    WatchStore.Instance.Load(solutionDir);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HoneyB] WatchStore load failed: {ex.Message}");
            }

            // Start listening to debugger events
            _listener = new HoneyBEventListener(this);
            _listener.Start();

            // Auto-show the HoneyB Explorer window (Chat + Explore + Watch tabs)
            // This runs every time the package loads (i.e. when a debug session starts).
            try
            {
                var exploreWindow = await ShowToolWindowAsync(
                    typeof(HoneyBExploreWindow),
                    id: 0,
                    create: true,
                    cancellationToken: cancellationToken);

                if (exploreWindow?.Frame is IVsWindowFrame frame)
                    frame.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[HoneyB] Could not show Explorer window: {ex.Message}");
            }

            // Register the commands so they are still invokable via keyboard/command bar
            await OpenHoneyBCommand.InitializeAsync(this);
            await OpenHoneyBTimelineCommand.InitializeAsync(this);
            await OpenHoneyBExploreCommand.InitializeAsync(this);
        }

        protected override void Dispose(bool disposing)
        {
            _listener?.Stop();
            base.Dispose(disposing);
        }
    }
}
