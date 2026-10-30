using Notch.Core.Agents;

namespace Notch.Core.Tests;

public class OpenChamberTests
{
    private static ProcessSnapshot P(int pid, int parent, string name, string? commandLine = null) => new(pid, parent, name, commandLine);

    [Fact]
    public void The_main_process_is_found_among_electrons_helpers()
    {
        ProcessSnapshot[] processes =
        [
            P(1, 0, "explorer.exe"),
            P(10, 1, "OpenChamber.exe", "\"C:\\Users\\me\\AppData\\Local\\Programs\\@openchamberelectron\\OpenChamber.exe\""),
            P(11, 10, "OpenChamber.exe", "OpenChamber.exe --type=renderer"),
            P(12, 10, "OpenChamber.exe", "OpenChamber.exe --type=gpu-process"),
            P(13, 10, "opencode.exe", "opencode.exe serve --stdio --hostname 127.0.0.1 --port 50123"),
        ];

        Assert.Equal(10, OpenChamber.MainPid(processes));
        Assert.Null(OpenChamber.MainPid([P(1, 0, "explorer.exe")]));
    }

    [Fact]
    public void Its_opencode_server_is_not_listed_as_an_opencode_window()
    {
        ProcessSnapshot[] processes =
        [
            P(10, 1, "OpenChamber.exe", "OpenChamber.exe"),
            P(13, 10, "opencode.exe", "opencode.exe serve --stdio --hostname 127.0.0.1 --port 50123"),
        ];

        Assert.Empty(AgentDetector.Detect(processes, ownPid: 999));
    }

    [Fact]
    public void Plugin_events_from_inside_openchamber_say_so_and_name_the_listed_session()
    {
        const string payload = """{"hook_event_name":"UserPromptSubmit","agent":"opencode","host":"openchamber","cwd":"C:\\code\\app","sessionID":"ses_child","listedSession":"ses_top","title":"Fix login"}""";
        Assert.True(AgentHooks.TryParseHookMessage(AgentHooks.FormatMessage("", payload, 13), out AgentHooks.HookMessage? message));

        Assert.True(OpenChamber.IsFrom(message!));
        Assert.Equal("ses_child", message!.AgentSession);
        Assert.Equal("ses_top", message.ListedSession);
        Assert.Equal("Fix login", message.Title);

        Assert.True(AgentHooks.TryParseHookMessage(AgentHooks.FormatMessage("", """{"hook_event_name":"Stop","agent":"opencode","sessionID":"ses_1"}""", 13), out AgentHooks.HookMessage? plain));
        Assert.False(OpenChamber.IsFrom(plain!));
        Assert.Equal("ses_1", plain!.ListedSession);
    }

    [Fact]
    public void Keys_and_links_name_the_session()
    {
        Assert.Equal("ses_1", OpenChamber.SessionOf(OpenChamber.KeyFor("ses_1")));
        Assert.Null(OpenChamber.SessionOf(OpenChamber.AppKey));
        Assert.Null(OpenChamber.SessionOf("pid-1-0"));
        Assert.Equal("openchamber://session/ses_1", OpenChamber.SessionLink("ses_1"));
    }

    [Fact]
    public void The_local_server_is_read_from_its_settings()
    {
        Assert.Equal((51234, "tok"), OpenChamber.ReadServer("""{"desktopLocalPort":51234,"desktopLocalClientToken":"tok","theme":"dark"}"""));
        Assert.Equal((51234, (string?)null), OpenChamber.ReadServer("""{"desktopLocalPort":51234}"""));
        Assert.Null(OpenChamber.ReadServer("""{"theme":"dark"}"""));
        Assert.Null(OpenChamber.ReadServer("not json"));
        Assert.Null(OpenChamber.ReadServer(null));
        Assert.EndsWith(Path.Combine(".config", "openchamber", "settings.json"), OpenChamber.SettingsPath(null, "home"));
        Assert.Equal(Path.Combine("D:", "oc", "settings.json"), OpenChamber.SettingsPath(Path.Combine("D:", "oc"), "home"));
    }

    [Fact]
    public void A_new_session_is_asked_for_by_folder_and_its_id_found_in_the_answer()
    {
        Assert.Contains("\"session.create\"", OpenChamber.NewSessionRequest(@"C:\code\app"));
        Assert.Contains(@"C:\\code\\app", OpenChamber.NewSessionRequest(@"C:\code\app"));

        Assert.Equal("ses_9", OpenChamber.ReadSessionId("""{"ok":true,"result":{"session":{"id":"ses_9","title":"New"}}}"""));
        Assert.Equal("ses_9", OpenChamber.ReadSessionId("""{"data":[{"sessionID":"ses_9"}]}"""));
        Assert.Null(OpenChamber.ReadSessionId("""{"ok":false,"error":"no"}"""));
        Assert.Null(OpenChamber.ReadSessionId("oops"));
    }
}
