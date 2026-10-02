using Notch.Core.Activities;
using Notch.Core.Automation;

namespace Notch.Core.Tests;

public class WebhookRequestTests
{
    private const string Token = "s3cret-token";
    private const int Port = 47890;

    private static WebhookRefusal? Authorize(string? host = "127.0.0.1:47890", string? origin = null, string? auth = "Bearer " + Token) =>
        WebhookRequest.Authorize(host, origin, auth, Token, Port);

    [Fact]
    public void A_local_caller_with_the_token_is_let_in()
    {
        Assert.Null(Authorize());
        Assert.Null(Authorize(host: "localhost:47890"));
        Assert.Null(Authorize(auth: "bearer " + Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer ")]
    [InlineData("Basic s3cret-token")]
    [InlineData(Token)]
    public void A_missing_or_wrong_token_is_refused(string? header)
    {
        Assert.Equal(401, Authorize(auth: header)?.Status);
    }

    [Fact]
    public void Web_pages_are_turned_away_even_with_the_token()
    {
        Assert.Equal(403, Authorize(origin: "https://evil.example")?.Status);
        Assert.Equal(403, Authorize(origin: "null")?.Status);
    }

    [Theory]
    [InlineData("evil.example:47890")]
    [InlineData("evil.example")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:1")]
    [InlineData(null)]
    public void A_name_that_was_rebound_to_this_computer_is_refused(string? host)
    {
        Assert.Equal(403, Authorize(host: host)?.Status);
    }

    [Fact]
    public void Without_a_token_set_nothing_is_accepted()
    {
        Assert.Equal(503, WebhookRequest.Authorize("127.0.0.1:47890", null, "Bearer ", "", Port)?.Status);
    }

    private static WebhookAction Route(string method, string path, string? body = null)
    {
        WebhookRefusal? refusal = WebhookRequest.Route(method, path, body, out WebhookAction? action);
        Assert.Null(refusal);
        return action!;
    }

    private static int Refuse(string method, string path, string? body = null) =>
        WebhookRequest.Route(method, path, body, out _)?.Status ?? 0;

    [Fact]
    public void An_activity_is_namespaced_and_clamped()
    {
        var publish = Assert.IsType<PublishActivityAction>(Route("POST", "/v1/activity", """
            { "id": "build", "title": "Building", "detail": "step 2", "glyph": "!", "tier": "ongoing",
              "progress": 3, "glow": { "color": "Cyan", "pattern": "breathe", "strength": 0.5 } }
            """));
        Activity activity = publish.Activity;
        Assert.Equal("webhook.build", activity.Id);
        Assert.Equal(ActivityTier.Ongoing, activity.Tier);
        Assert.Equal(1.0, activity.Progress);
        Assert.Equal(new Glow(GlowColor.Cyan, GlowPattern.Breathe, 0.5), activity.Glow);
        Assert.Null(activity.Lifetime);
    }

    [Fact]
    public void A_plain_activity_is_transient_with_a_capped_lifetime()
    {
        var publish = Assert.IsType<PublishActivityAction>(Route("POST", "/v1/activity", """{ "id": "a", "title": "T", "lifetimeMs": 99999999, "glow": "green" }"""));
        Assert.Equal(ActivityTier.Transient, publish.Activity.Tier);
        Assert.Equal(WebhookRequest.MaxTransientLifetime, publish.Activity.Lifetime);
        Assert.Equal(GlowColor.Green, publish.Activity.Glow!.Color);
    }

    [Theory]
    [InlineData("""{ "title": "no id" }""")]
    [InlineData("""{ "id": "has space", "title": "T" }""")]
    [InlineData("""{ "id": "a", "title": "" }""")]
    [InlineData("""{ "id": "a", "title": "T", "tier": "urgent" }""")]
    [InlineData("""{ "id": "a", "title": "T", "glow": "sparkly" }""")]
    [InlineData("""{ "id": "a", "title": "T", "glow": { "color": "red", "pattern": "wobble" } }""")]
    [InlineData("[1]")]
    [InlineData("nope")]
    public void A_bad_activity_is_refused_with_400(string body)
    {
        Assert.Equal(400, Refuse("POST", "/v1/activity", body));
    }

    [Fact]
    public void An_activity_can_be_removed_by_id()
    {
        Assert.Equal(new RemoveActivityAction("webhook.build"), Route("DELETE", "/v1/activity/build"));
        Assert.Equal(400, Refuse("DELETE", "/v1/activity/bad%20id"));
        Assert.Equal(405, Refuse("GET", "/v1/activity/build"));
    }

    [Fact]
    public void Notify_and_command_use_the_shared_commands()
    {
        var toast = Assert.IsType<RunCommandAction>(Route("POST", "/v1/notify", """{ "title": "Done", "color": "green" }"""));
        Assert.IsType<ToastCommand>(toast.Command);

        var timer = Assert.IsType<RunCommandAction>(Route("POST", "/v1/command", """{ "command": "timer", "d": "5m" }"""));
        Assert.Equal(new StartTimerCommand(TimeSpan.FromMinutes(5)), timer.Command);
    }

    [Fact]
    public void Plugins_cannot_be_installed_or_switched_from_the_webhook()
    {
        Assert.Equal(403, Refuse("POST", "/v1/command", """{ "command": "install", "id": "acme.x" }"""));
        Assert.Equal(403, Refuse("POST", "/v1/command", """{ "command": "plugin", "id": "acme.x", "enable": true }"""));
    }

    [Fact]
    public void Unknown_paths_and_methods_are_refused()
    {
        Assert.Equal(404, Refuse("POST", "/v2/activity", "{}"));
        Assert.Equal(404, Refuse("POST", "/", "{}"));
        Assert.Equal(405, Refuse("GET", "/v1/notify"));
        Assert.Equal(400, Refuse("POST", "/v1/command", """{ "command": "fly" }"""));
        Assert.Equal(400, Refuse("POST", "/v1/command", "{}"));
    }

    [Fact]
    public void A_missing_or_huge_body_is_refused()
    {
        Assert.Equal(413, Refuse("POST", "/v1/notify", null));
        Assert.Equal(413, Refuse("POST", "/v1/notify", new string('x', WebhookRequest.MaxBodyBytes + 1)));
    }

    [Fact]
    public void Tokens_are_long_and_different_each_time()
    {
        string a = WebhookRequest.NewToken();
        Assert.True(a.Length >= 40);
        Assert.NotEqual(a, WebhookRequest.NewToken());
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
    }
}
