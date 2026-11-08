using Velopack;
using AgentsCursor.App.Services;
using AgentsCursor.Core;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// What the control panel needs from the app that owns it (the tray). The panel holds no settings of
/// its own: it reads <see cref="Settings"/> and writes each edit straight through
/// <see cref="LiveSettings.Change"/>, so every change applies at once and nothing is ever written back
/// from a stale copy. All members are used on the UI thread.
/// </summary>
public interface IControlPanelHost
{
    LiveSettings Settings { get; }
    IAutostart Autostart { get; }
    UpdateService Updates { get; }

    /// <summary>Shows that many pretend agents (all blocked on you) on the real token, or with null
    /// ends the preview and shows the real ones again.</summary>
    void Preview(int? waitingAgents);

    /// <summary>Exits so <paramref name="release"/> can be installed, then the new version starts.
    /// Throws, leaving everything running, if the updater can't be started.</summary>
    void RestartToUpdate(VelopackAsset release);
}
