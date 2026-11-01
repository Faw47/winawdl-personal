using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using Winawdl.Shell;

namespace Winawdl.ShareHelper;

/// <summary>Started by Windows when AirDrop is picked in the Share sheet: takes what
/// was shared and hands it to winawdl.exe, starting it first if it is not running.
/// It never starts winawdl.exe itself, which would then run inside this package.</summary>
public static class Program
{
    private static readonly TimeSpan Running = TimeSpan.FromSeconds(2), Starting = TimeSpan.FromSeconds(30);

    public static async Task<int> Main()
    {
        if (AppInstance.GetActivatedEventArgs() is not ShareTargetActivatedEventArgs { ShareOperation: { } operation })
            return 2;
        try
        {
            var shared = await Share.Gather(new Source(operation.Data),
                                            Path.Combine(Share.StashRoot, Guid.NewGuid().ToString("N")));
            // The sharing app may now let go: what it lent is copied or was never lent.
            operation.ReportDataRetrieved();
            if (shared.Paths.Length == 0 && shared.Link.Length == 0)
            {
                operation.ReportError("There was nothing AirDrop can send: share files or a web link.");
                return 1;
            }
            var pipe = Share.PipeName("winawdl-app");
            if (!await Share.Forward(shared, pipe, Running))
            {
                // Launched by Windows, not by this process, so it runs as winawdl normally does.
                await Windows.System.Launcher.LaunchUriAsync(new Uri(Share.OpenUri));
                if (!await Share.Forward(shared, pipe, Starting))
                {
                    operation.ReportError("winawdl did not open. Start it from the Start Menu and share again.");
                    return 1;
                }
            }
            operation.ReportCompleted();
            return 0;
        }
        catch (Exception error)
        {
            // Whatever failed, the sharing app is told, rather than left waiting.
            operation.ReportError("winawdl could not take what was shared: " + error.Message);
            return 1;
        }
    }

    private sealed class Source(DataPackageView data) : Share.ISource
    {
        public async Task<IReadOnlyList<Share.Item>> Items()
        {
            if (!data.Contains(StandardDataFormats.StorageItems)) return [];
            return (await data.GetStorageItemsAsync()).Select(item => new Share.Item(
                item.Path ?? "", item is IStorageFolder, async folder =>
                {
                    var target = await StorageFolder.GetFolderFromPathAsync(folder);
                    var copy = await ((IStorageFile)item).CopyAsync(target, item.Name, NameCollisionOption.GenerateUniqueName);
                    return copy.Path;
                })).ToList();
        }

        public async Task<string?> Link()
        {
            if (data.Contains(StandardDataFormats.WebLink)) return (await data.GetWebLinkAsync()).AbsoluteUri;
            if (data.Contains(StandardDataFormats.Text)) return (await data.GetTextAsync()).Trim();
            return null;
        }
    }
}
