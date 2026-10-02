using System.IO;
using Notch.Core.Activities;
using Notch.Core.Agents;
using Notch.Core.Hud;

namespace Notch.App.Shell;

/// <summary>
/// Saves a picture of every notch state the website shows (<c>--screenshots=&lt;folder&gt;</c>, with <c>--demo</c>).
/// The pictures are the real window rendered by the real code, so the site cannot drift from the app.
/// </summary>
internal static class ScreenshotRunner
{
    public static async Task RunAsync(NotchWindow window, ActivityManager activities, string folder)
    {
        string File(string name) => Path.Combine(folder, name + ".png");

        async Task Compact(string name, Activity activity)
        {
            activities.Publish(activity);
            await window.WaitUntilSettledAsync();
            window.SavePicture(File(name));
            activities.Remove(activity.Id);
        }

        await window.WaitUntilSettledAsync();
        window.SavePicture(File("pill-media"));

        string agent = AgentActivities.IdFor("shot");
        await Compact("pill-agent-working", AgentActivities.For("shot", "Claude", "", AgentState.Working)!);
        await Compact("pill-agent-needs-input", AgentActivities.For("shot", "Claude", "", AgentState.NeedsInput)!);
        await Compact("pill-agent-done", AgentActivities.For("shot", "Claude", "", AgentState.Done)!);
        activities.Remove(agent);
        await Compact("pill-volume", HudActivities.Volume(0.6, muted: false));
        await Compact("pill-charging", HudActivities.Power(pluggedIn: true, percent: 82));
        await Compact("pill-bluetooth", HudActivities.Bluetooth("Headphones", connected: true));
        await Compact("pill-low-battery", HudActivities.LowBattery(9));
        await Compact("pill-caps-lock", HudActivities.CapsLock(on: true));

        window.SetPinned(true);
        window.ShowTab("home");
        await window.WaitUntilSettledAsync();
        window.SavePicture(File("home"));

        window.ShowTab("stats");
        await window.WaitUntilSettledAsync();
        await Task.Delay(10000);
        window.SavePicture(File("stats"));

        window.ShowTab("shelf");
        await window.WaitUntilSettledAsync();
        window.SavePicture(File("shelf"));

        // Made-up agents, so the picture never shows what the machine running it has open.
        window.ShowTab("home");
        window.SeedDemoAgents();
        await window.WaitUntilSettledAsync();
        window.SavePicture(File("home-agents"));
        window.ClearDemoAgents();

        window.SeedDemoClipboard();
        window.ShowTab("clipboard");
        await window.WaitUntilSettledAsync();
        window.SavePicture(File("clipboard"));

        window.ShowDemoPalette("");
        await window.WaitUntilSettledAsync();
        window.SavePicture(File("palette"));
        window.ShowDemoPalette("tim");
        await window.WaitUntilSettledAsync();
        window.SavePicture(File("palette-search"));
        window.ShowDemoPalette(null);
    }
}
