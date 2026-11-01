using System.Security.Cryptography;
using Winawdl.Model;

namespace Winawdl.Shell;

/// <summary>Notification actions carry random, short-lived capabilities, never received paths.</summary>
public sealed class NoticeActivation(TableModel model, Func<bool> inFront, IToast toast,
                                     Action requestCard, Action<ArrivalModel> arrivalCard,
                                     Func<DateTimeOffset> now) : IDisposable
{
    internal const string Prefix = "winawdl-notice://action/";
    private readonly Dictionary<string, (IncomingModel? Item, bool? Accept, DateTimeOffset Until)> _actions = [];
    private bool _disposed;

    internal static bool Valid(string uri) => uri.Length == Prefix.Length + 64
        && uri.StartsWith(Prefix, StringComparison.Ordinal)
        && uri[Prefix.Length..].All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string Action(IncomingModel? item = null, bool? accept = null)
    {
        foreach (var key in _actions.Where(pair => pair.Value.Until <= now()).Select(pair => pair.Key).ToArray())
            _actions.Remove(key);
        var uri = Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _actions[uri] = (item, accept, now().AddMinutes(2));
        return uri;
    }

    public void Request()
    {
        if (!model.ShowRequests || inFront()) return;
        var item = model.Incoming.FirstOrDefault(item => item.IsAsking);
        if (item is null) return;
        var notice = new Notice(item.Headline, item.Detail, Action(), Action(item, true), Action(item, false));
        if (!toast.Show(notice, () => { if (model.ShowRequests && !inFront()) requestCard(); })) requestCard();
    }

    public void Arrival(ArrivalModel arrival)
    {
        if (_disposed || !model.ShowRequests || inFront()) return;
        // Only an arrival already stored on disk has photo bytes. An incoming
        // request must never decode untrusted files before consent.
        if (ToastPreviews.Candidate(arrival.File.Path))
            _ = PhotoArrival(arrival);
        else ShowArrival(arrival, "");
    }

    private async Task PhotoArrival(ArrivalModel arrival)
    {
        var preview = await ToastPreviews.CreateAsync(arrival.File.Path);
        // A timed-out decode must not display an old toast after shutdown.
        if (!_disposed) ShowArrival(arrival, preview);
    }

    private void ShowArrival(ArrivalModel arrival, string preview)
    {
        if (_disposed || !model.ShowRequests || inFront()) return;
        if (!toast.Show(new Notice(arrival.Headline, arrival.Detail, Action(), Preview: preview),
                () => { if (!_disposed && model.ShowRequests && !inFront()) arrivalCard(arrival); }))
            arrivalCard(arrival);
    }

    public bool Activate(string uri)
    {
        if (!Valid(uri) || !_actions.Remove(uri, out var action) || action.Until <= now()) return false;
        if (action.Accept is { } accept && action.Item is { IsAsking: true } item
            && model.Incoming.Contains(item))
        {
            foreach (var key in _actions.Where(pair => ReferenceEquals(pair.Value.Item, item))
                         .Select(pair => pair.Key).ToArray()) _actions.Remove(key);
            if (accept) item.Accept?.Execute(null);
            else item.Decline?.Execute(null);
        }
        return true;
    }

    public void Dispose() { _disposed = true; _actions.Clear(); toast.Clear(); }
}
