using System.ComponentModel;
using System.Windows.Media;
using AgentsCursor.App.Services;

namespace AgentsCursor.App.ControlPanel;

/// <summary>What a tool card's main button does.</summary>
public enum ToolAction { Connect, Disconnect, ShowFile }

/// <summary>
/// One agent tool on the Agents page: its logo, where its config lives, and whether AgentsCursor is
/// connected to it (its hooks or its plugin are in that config) right now, with the actions that make
/// sense for that state. Every tool's card reads the same way.
/// </summary>
public sealed class ToolRow : INotifyPropertyChanged
{
    public ToolRow(HookTool tool, ImageSource logo)
    {
        Tool = tool;
        Logo = logo;
    }

    public HookTool Tool { get; }
    public ImageSource Logo { get; }
    public string Name => Tool.DisplayName;
    public string Detail => Tool.Detail;
    public string ConfigPath { get; private set; } = "";

    public HookState State { get; private set; }

    /// <summary>How AgentsCursor changed this connection during this run, if it did.</summary>
    public ConnectionChange? Change { get; private set; }

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
        HookState.Registered => Change switch
        {
            ConnectionChange.RefreshedAtStartup => "Updated automatically when AgentsCursor started. " + Tool.AfterChange,
            ConnectionChange.ByYou => Tool.AfterChange,
            _ => "",
        },
        HookState.Outdated => "AgentsCursor's connection points at an older copy or an older setup. Update it so every event arrives.",
        HookState.ConfigConflict => "This file isn't valid, so AgentsCursor won't change it. Fix or remove it, then come back here.",
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

    /// <summary>Re-reads the tool's config; <paramref name="change"/> is how this run changed it, if it did.</summary>
    public void Refresh(ConnectionChange? change)
    {
        Change = change;
        ConfigPath = AgentHookRegistrar.ConfigPath(Tool.Id);
        State = AgentHookRegistrar.GetState(Tool.Id);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // every derived property
    }
}
