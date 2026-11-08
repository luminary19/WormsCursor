using System.ComponentModel;
using System.Windows.Media;
using WormsCursor.App.Services;

namespace WormsCursor.App.ControlPanel;

/// <summary>
/// One agent tool on the Agents page: its logo, where its config lives, and what WormsCursor's hook
/// registration there looks like right now, with the actions that make sense for that state.
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
        HookState.Registered => "Registered",
        HookState.Outdated => "Update available",
        HookState.ConfigConflict => "Config needs fixing",
        _ => "Not registered",
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
        HookState.Outdated => "An older WormsCursor registered these hooks. Update them so every event arrives.",
        HookState.ConfigConflict => "The config file couldn't be read. Fix it by hand, then register again.",
        _ => "",
    };

    /// <summary>The main button: register, update, or unregister.</summary>
    public string PrimaryText => State switch
    {
        HookState.Registered => "Unregister",
        HookState.Outdated => "Update",
        _ => "Register",
    };

    /// <summary>Whether the main button is the accent (call-to-action) one.</summary>
    public bool PrimaryIsAccent => State != HookState.Registered;

    /// <summary>Whether the main button writes our hooks (register / update) rather than removing them.</summary>
    public bool PrimaryRegisters => State != HookState.Registered;

    /// <summary>"Unregister" next to "Update" for an outdated registration.</summary>
    public bool ShowUnregister => State == HookState.Outdated;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-reads the tool's config.</summary>
    public void Refresh()
    {
        State = AgentHookRegistrar.GetState(Tool.Id);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); // every derived property
    }
}
