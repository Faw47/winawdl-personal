using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Winawdl.Theme;

/// <summary>LiveSetting describes a region; WPF also needs its change event.</summary>
public sealed class LiveText : TextBlock
{
    private bool _queued;

    static LiveText() => TextProperty.OverrideMetadata(typeof(LiveText),
        new FrameworkPropertyMetadata((owner, _) => ((LiveText)owner).Announce()));

    public LiveText()
    {
        Loaded += (_, _) => Announce();
        IsVisibleChanged += (_, _) => Announce();
    }

    private void Announce()
    {
        if (_queued || !IsLoaded || !IsVisible || Text.Length == 0
            || AutomationProperties.GetLiveSetting(this) == AutomationLiveSetting.Off) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _queued = false;
            if (IsVisible && Text.Length > 0 && AutomationProperties.GetLiveSetting(this) != AutomationLiveSetting.Off)
                UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }
}
