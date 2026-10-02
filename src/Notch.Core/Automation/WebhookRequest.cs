using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Notch.Core.Activities;

namespace Notch.Core.Automation;

/// <summary>What a webhook request asks for, once it has been checked.</summary>
public abstract record WebhookAction;

public sealed record PublishActivityAction(Activity Activity) : WebhookAction;

public sealed record RemoveActivityAction(string ActivityId) : WebhookAction;

public sealed record RunCommandAction(NotchCommand Command) : WebhookAction;

/// <summary>A request that was turned away, with the HTTP status and a message for the caller.</summary>
public sealed record WebhookRefusal(int Status, string Message);

/// <summary>
/// The rules of the local webhook, with no sockets in them so they can be tested: who may call,
/// what the paths are, and what a body may say. The server only moves bytes.
/// </summary>
public static partial class WebhookRequest
{
    public const int DefaultPort = 47890;
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxIdLength = 64;
    public const string ActivityPrefix = "webhook.";
    public static readonly TimeSpan MaxTransientLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Decides whether the caller may use the webhook at all. A browser page cannot pass this: it
    /// always sends an <c>Origin</c>, and a page that rebinds its own name to this computer carries
    /// that name in <c>Host</c>. Anything else must know the token.
    /// </summary>
    public static WebhookRefusal? Authorize(string? host, string? origin, string? authorization, string? token, int port)
    {
        if (!string.IsNullOrEmpty(origin))
        {
            return new WebhookRefusal(403, "Requests from web pages are not accepted.");
        }

        if (!IsLocalHost(host, port))
        {
            return new WebhookRefusal(403, "Use the address 127.0.0.1 or localhost.");
        }

        if (string.IsNullOrEmpty(token))
        {
            return new WebhookRefusal(503, "The webhook has no token yet.");
        }

        const string bearer = "Bearer ";
        if (authorization is null || !authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            || !SameSecret(authorization[bearer.Length..].Trim(), token))
        {
            return new WebhookRefusal(401, "Send the token as Authorization: Bearer <token>.");
        }

        return null;
    }

    /// <summary>Reads a checked request. Returns null for the refusal when the request is fine.</summary>
    public static WebhookRefusal? Route(string method, string path, string? body, out WebhookAction? action)
    {
        action = null;
        string clean = path.Split('?')[0].TrimEnd('/');

        if (clean.StartsWith("/v1/activity/", StringComparison.Ordinal))
        {
            if (!method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                return new WebhookRefusal(405, "Use DELETE to remove an activity.");
            }

            string id = Uri.UnescapeDataString(clean["/v1/activity/".Length..]);
            if (!IsValidId(id))
            {
                return new WebhookRefusal(400, "That id is not valid.");
            }

            action = new RemoveActivityAction(ActivityPrefix + id);
            return null;
        }

        if (clean is not ("/v1/activity" or "/v1/notify" or "/v1/command"))
        {
            return new WebhookRefusal(404, "Unknown path.");
        }

        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            return new WebhookRefusal(405, "Use POST.");
        }

        if (body is null || Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
        {
            return new WebhookRefusal(413, "The body is missing or too large.");
        }

        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new WebhookRefusal(400, "The body is not valid JSON.");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new WebhookRefusal(400, "The body must be a JSON object.");
        }

        return clean switch
        {
            "/v1/activity" => ReadActivity(root, out action),
            "/v1/notify" => ReadCommand("toast", root, out action),
            _ => ReadCommand(Text(root, "command") ?? Text(root, "verb"), root, out action),
        };
    }

    private static WebhookRefusal? ReadCommand(string? verb, JsonElement root, out WebhookAction? action)
    {
        action = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            string? text = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                _ => null,
            };
            if (text is not null)
            {
                values[property.Name] = text;
            }
        }

        // The webhook may start timers and show notices; installing plugins stays a thing the user does at their own screen.
        NotchCommand? command = null;
        string? error = null;
        if (verb is null || !CommandParser.Build(verb, values, out command, out error))
        {
            return new WebhookRefusal(400, error ?? "Say which command to run.");
        }

        if (command is InstallPluginCommand or SetPluginEnabledCommand)
        {
            return new WebhookRefusal(403, "Plugins cannot be installed or switched through the webhook.");
        }

        action = new RunCommandAction(command!);
        return null;
    }

    private static WebhookRefusal? ReadActivity(JsonElement root, out WebhookAction? action)
    {
        action = null;
        string? id = Text(root, "id");
        if (!IsValidId(id))
        {
            return new WebhookRefusal(400, "An activity needs an id of letters, digits, dots, dashes or underscores.");
        }

        string? title = Text(root, "title");
        if (string.IsNullOrWhiteSpace(title) || title.Length > CommandParser.MaxTitleLength)
        {
            return new WebhookRefusal(400, $"The title must be 1 to {CommandParser.MaxTitleLength} characters.");
        }

        string? detail = Text(root, "detail");
        if (detail is { Length: > CommandParser.MaxDetailLength })
        {
            return new WebhookRefusal(400, $"The detail can be at most {CommandParser.MaxDetailLength} characters.");
        }

        string? glyph = Text(root, "glyph");
        if (glyph is { Length: > CommandParser.MaxGlyphLength })
        {
            return new WebhookRefusal(400, "The glyph is one symbol.");
        }

        ActivityTier tier;
        switch (Text(root, "tier")?.ToLowerInvariant())
        {
            case null or "transient":
                tier = ActivityTier.Transient;
                break;
            case "ongoing":
                tier = ActivityTier.Ongoing;
                break;
            case "attention":
                tier = ActivityTier.Attention;
                break;
            default:
                return new WebhookRefusal(400, "The tier is ongoing, attention or transient.");
        }

        double? progress = null;
        if (root.TryGetProperty("progress", out JsonElement progressValue) && progressValue.ValueKind == JsonValueKind.Number)
        {
            double value = progressValue.GetDouble();
            progress = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
        }

        Glow? glow = null;
        if (root.TryGetProperty("glow", out JsonElement glowValue) && glowValue.ValueKind != JsonValueKind.Null)
        {
            glow = ReadGlow(glowValue);
            if (glow is null)
            {
                return new WebhookRefusal(400, "A glow is a colour name, #rrggbb, or { color, pattern, strength }.");
            }
        }

        TimeSpan? lifetime = null;
        if (root.TryGetProperty("lifetimeMs", out JsonElement lifetimeValue) && lifetimeValue.ValueKind == JsonValueKind.Number
            && lifetimeValue.TryGetInt64(out long milliseconds) && milliseconds > 0)
        {
            lifetime = TimeSpan.FromMilliseconds(Math.Min(milliseconds, MaxTransientLifetime.TotalMilliseconds));
        }

        action = new PublishActivityAction(new Activity
        {
            Id = ActivityPrefix + id,
            Tier = tier,
            Title = title.Trim(),
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
            Glyph = string.IsNullOrEmpty(glyph) ? null : glyph,
            Progress = progress,
            Glow = glow,
            Lifetime = tier == ActivityTier.Transient ? lifetime : null,
        });
        return null;
    }

    private static Glow? ReadGlow(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return CommandParser.ParseColor(value.GetString()) is { } color ? new Glow(color, GlowPattern.Steady) : null;
        }

        if (value.ValueKind != JsonValueKind.Object || CommandParser.ParseColor(Text(value, "color")) is not { } chosen)
        {
            return null;
        }

        GlowPattern pattern = GlowPattern.Steady;
        if (Text(value, "pattern") is { } name && !Enum.TryParse(name, ignoreCase: true, out pattern))
        {
            return null;
        }

        double strength = value.TryGetProperty("strength", out JsonElement s) && s.ValueKind == JsonValueKind.Number
            ? Math.Clamp(s.GetDouble(), 0, 1)
            : 1;
        return new Glow(chosen, pattern, strength);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= MaxIdLength } && IdPattern().IsMatch(id);

    /// <summary><c>127.0.0.1:port</c> or <c>localhost:port</c> (also without a port, which a client may leave out for port 80 only).</summary>
    private static bool IsLocalHost(string? host, int port) =>
        host is not null
        && (host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase));

    private static bool SameSecret(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    /// <summary>A new random secret for the webhook: 32 random bytes in URL-safe base64.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex IdPattern();
}
