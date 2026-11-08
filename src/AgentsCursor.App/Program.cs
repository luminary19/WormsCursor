using System.Windows.Forms;
using Velopack;
using AgentsCursor.App.Services;

namespace AgentsCursor.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Agent-notifier bridge: `AgentsCursor.exe hook …` is a throwaway invocation fired by an
        // AI agent's hook. Handle it FIRST — before Velopack and the single-instance guard — so
        // it starts lean and never spins up UI: it forwards one event over the pipe and exits 0.
        // Fail-silent (it must never block or crash the agent), so this can't throw.
        if (AgentHookBridge.IsHookInvocation(args))
        {
            AgentHookBridge.Run(args);
            return;
        }

        // Headless hook (un)registration: `AgentsCursor.exe register|unregister [--tool <id>]` writes or
        // removes our hooks in the tool's config and exits — the same action as the Agent-settings
        // dialog's buttons, but scriptable and UI-less (setup scripts; refreshing an outdated set).
        // A script must be able to tell it failed (unknown tool id, malformed config, …): the reason
        // goes to stderr (if the caller captured it) and bridge.log, and the exit code is non-zero.
        if (args.Length > 0 && args[0] is "register" or "unregister")
        {
            string tool = ArgValue(args, "--tool") ?? "claude-code";
            try
            {
                if (args[0] == "register") AgentHookRegistrar.Register(tool);
                else AgentHookRegistrar.Unregister(tool);
            }
            catch (Exception ex)
            {
                string error = $"{args[0]} --tool {tool} failed: {ex.Message}";
                Console.Error.WriteLine(error);
                AppLog.Write(error);
                Environment.ExitCode = 1;
            }
            return;
        }

        // Velopack hijacks Main when invoked with --veloapp-* args during
        // install / update / uninstall: it runs the matching hook and exits
        // before any UI spins up. Must stay the very first call in Main.
        // No-ops cleanly for dev builds run straight out of bin\.
        VelopackApp.Build().Run();

        // Single-instance guard, before any UI: if another instance is already
        // running, Acquire() signals it to open Preferences and returns null, so we
        // exit immediately — no tray icon, no cursor takeover.
        using var single = SingleInstance.Acquire();
        if (single is null) return;

        // High-DPI mode etc. comes from ApplicationHighDpiMode in the .csproj.
        ApplicationConfiguration.Initialize();

        // No main window: the whole app lives in the tray.
        var tray = new TrayApplicationContext();
        single.StartListening(tray.OpenPreferences);
        Application.Run(tray);
    }

    static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
