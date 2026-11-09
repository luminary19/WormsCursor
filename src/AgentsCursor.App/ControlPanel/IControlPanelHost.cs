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

    /// <summary>The tools whose connection AgentsCursor changed during this run, and how: the Agents
    /// page then shows the step that tool needs before the change takes effect.</summary>
    IReadOnlyDictionary<string, ConnectionChange> ChangedThisRun { get; }

    /// <summary>Why the startup refresh couldn't bring a tool's outdated connection up to date.</summary>
    IReadOnlyDictionary<string, string> RefreshErrors { get; }

    /// <summary>The user connected (or updated) <paramref name="toolId"/> on the Agents page.</summary>
    void NoteConnected(string toolId);

    /// <summary>The user disconnected <paramref name="toolId"/> on the Agents page.</summary>
    void NoteDisconnected(string toolId);

    /// <summary>Shows that many pretend agents (all blocked on you) on the real token, or with null
    /// ends the preview and shows the real ones again.</summary>
    void Preview(int? waitingAgents);

    /// <summary>Plays <paramref name="chime"/> at the current volume (the Sounds play buttons).</summary>
    void PlayChime(Chime chime);

    /// <summary>Whether the token is on screen and, if not, why.</summary>
    TokenVisibility TokenVisibility { get; }

    /// <summary>Raised on the UI thread when <see cref="TokenVisibility"/> changes.</summary>
    event Action? TokenVisibilityChanged;

    /// <summary>Exits so <paramref name="release"/> can be installed, then the new version starts.
    /// Throws, leaving everything running, if the updater can't be started.</summary>
    void RestartToUpdate(VelopackAsset release);
}

/// <summary>How a tool's connection changed during this run.</summary>
public enum ConnectionChange
{
    /// <summary>AgentsCursor updated an outdated connection when it started.</summary>
    RefreshedAtStartup,
    /// <summary>The user connected or updated it on the Agents page.</summary>
    ByYou,
}
