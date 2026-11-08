using System.ComponentModel;
using System.Windows.Media;
using WormsCursor.App.Services;

namespace WormsCursor.App.ControlPanel;

/// <summary>What a tool card's main button does.</summary>
public enum ToolAction { Connect, Disconnect, ShowFile }

/// <summary>
/// One agent tool on the Agents page: its logo, where its config lives, and whether WormsCursor is
/// connected to it (its hooks are in that config) right now, with the actions that make sense for that
/// state.
/// </summary>
public sealed class ToolRow : INotifyPropertyChanged
{
    public ToolRow(HookTool tool, ImageSource logo, string configPath)
    {
        Tool = tool;
        Logo = logo;
        ConfigPath = configPath;
    }

    public HookTool Tool { get; }
    public ImageSource Logo { get; }
    public string Name => Tool.DisplayName;
    public string ConfigPath { get; }

    public HookState State { get; private set; }

    public string StatusText => State switch
    {
        HookState.Registered => "Connected",
        HookState.Outdated => "Needs update",
        HookState.ConfigConflict => "Can't read its config",
        _ => "Not connected",
    };

    /// <summary>The status dot: "good", "warn", "error" or "off" (the page maps these to theme brushes).</summary>
    public string Tone => State switch
    {
        HookState.Registered => "good",
        HookState.Outdated => "warn",
        HookState.ConfigConflict => "error",
        _ => "off",
    };

    public string Explanation => State switch
    {
        HookState.Registered => Tool.SetupNote ?? "",
        HookState.Outdated => "An older WormsCursor connected these hooks. Update them so every event arrives.",
        HookState.ConfigConflict => "This file isn't valid, so WormsCursor won't change it. Fix or remove it, then come back here.",
        _ => "",
    };

    /// <summary>The main button: connect (or update), disconnect, or, when the config can't be read,
    /// show the file so it can be fixed by hand — connecting can't succeed until it parses.</summary>
    public ToolAction PrimaryAction => State switch
    {
        HookState.Registered => ToolAction.Disconnect,
        HookState.ConfigConflict => ToolAction.ShowFile,
        _ => ToolAction.Connect,
    };

    public string PrimaryText => State switch
    {
        HookState.Registered => "Disconnect",
        HookState.Outdated => "Update",
        HookState.ConfigConflict => "Show file",
        _ => "Connect",
    };

    /// <summary>Whether the main button is the accent (call-to-action) one: only when it connects.</summary>
    public bool PrimaryIsAccent => PrimaryAction == ToolAction.Connect;

    /// <summary>"Disconnect" next to "Update" for an outdated connection.</summary>
    public bool ShowDisconnect => State == HookState.Outdated;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-reads the tool's config.</summary>
    public void Refresh()
    {
        State = AgentHookRegistrar.GetState(Tool.Id);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // every derived property
    }
}
