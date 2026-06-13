using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace HoneyB
{
    /// <summary>
    /// Adds a "HoneyB Explorer" entry under View > Other Windows.
    /// Opens the tabbed Explore/Watch/Chat tool window.
    /// </summary>
    internal sealed class OpenHoneyBExploreCommand
    {
        // Use a different CommandID from the other commands
        private static readonly Guid CommandSetGuid = new Guid("f1a2b3c4-d5e6-7890-fabc-de1234567890");
        private const int CommandId = 0x0300;

        private readonly AsyncPackage _package;

        private OpenHoneyBExploreCommand(AsyncPackage package, IMenuCommandService commandService)
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
            new OpenHoneyBExploreCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                var window = await _package.ShowToolWindowAsync(
                    typeof(HoneyBExploreWindow),
                    id: 0,
                    create: true,
                    cancellationToken: _package.DisposalToken);

                if (window?.Frame is IVsWindowFrame frame)
                    frame.Show();
            });
        }
    }
}
