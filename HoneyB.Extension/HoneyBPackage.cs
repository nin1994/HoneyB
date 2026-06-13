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
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideToolWindow(typeof(HoneyBChatWindow))]
    [ProvideToolWindow(typeof(HoneyBTimelineWindow))]
    [ProvideToolWindow(typeof(HoneyBExploreWindow))]
    [ProvideMenuResource("Menus.ctmenu", 1)]
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

            // Register the commands to open the tool windows
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
