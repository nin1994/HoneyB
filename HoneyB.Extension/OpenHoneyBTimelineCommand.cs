using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace HoneyB
{
    /// <summary>
    /// Adds an "HoneyB Timeline" entry under the View > Other Windows menu.
    /// </summary>
    internal sealed class OpenHoneyBTimelineCommand
    {
        // Must match the package/vsct GUIDs (if any) or be unique.
        private static readonly Guid CommandSetGuid = new Guid("c1d2e3f4-a5b6-7890-cdef-ab1234567890");
        private const int CommandId = 0x0101; // Next ID after OpenHoneyBCommand

        private readonly AsyncPackage _package;

        private OpenHoneyBTimelineCommand(AsyncPackage package, IMenuCommandService commandService)
        {
            _package = package;
            var menuCommandId = new CommandID(CommandSetGuid, CommandId);
            var menuItem = new MenuCommand(Execute, menuCommandId);
            commandService.AddCommand(menuItem);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
                as IMenuCommandService;
            new OpenHoneyBTimelineCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                var window = await _package.ShowToolWindowAsync(
                    typeof(HoneyBTimelineWindow),
                    id: 0,
                    create: true,
                    cancellationToken: _package.DisposalToken);

                if (window?.Frame is IVsWindowFrame frame)
                    frame.Show();
            });
        }
    }
}
