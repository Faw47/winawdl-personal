using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Winawdl.Theme;

/// <summary>The adjacent text or named button describes these decorations.</summary>
public sealed class DecorativeImage : Image
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}

public sealed class GlyphText : TextBlock
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
