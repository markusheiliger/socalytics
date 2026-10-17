using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SocAlytics.Platform.Application.Notifications;

namespace SocAlytics.Platform.Infrastructure.Email;

internal sealed partial class EmailRenderer : IEmailRenderer
{
    private const string ResourceName = "Email/Messages.en.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<IReadOnlyDictionary<string, MessageTemplate>> Templates = new(LoadTemplates);

    public RenderedEmail Render(string notificationType, EmailRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!Templates.Value.TryGetValue(notificationType ?? string.Empty, out var template))
        {
            throw new ArgumentException("The notification type has no email message.", nameof(notificationType));
        }

        var values = new Dictionary<string, string> { ["link"] = model.Link };
        if (!string.IsNullOrWhiteSpace(model.ClubName))
        {
            values["clubName"] = model.ClubName;
        }

        if (model.LinkLifetime is { } lifetime)
        {
            values["lifetime"] = FormatLifetime(lifetime);
        }

        return new RenderedEmail(
            Fill(template.Subject, values, encode: false),
            Fill(template.Text, values, encode: false),
            Fill(template.Html, values, encode: true));
    }

    internal static string FormatLifetime(TimeSpan lifetime)
    {
        if (lifetime >= TimeSpan.FromHours(1))
        {
            var hours = (int)Math.Round(lifetime.TotalHours);
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        var minutes = Math.Max(1, (int)Math.Round(lifetime.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private static string Fill(string template, Dictionary<string, string> values, bool encode) =>
        PlaceholderPattern().Replace(template, match =>
        {
            if (!values.TryGetValue(match.Groups[1].Value, out var value))
            {
                throw new InvalidOperationException($"The message needs the value '{match.Groups[1].Value}'.");
            }

            return encode ? WebUtility.HtmlEncode(value) : value;
        });

    private static Dictionary<string, MessageTemplate> LoadTemplates()
    {
        using var stream = typeof(EmailRenderer).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The email message resource is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, MessageTemplate>>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The email message resource is empty.");
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();

    private sealed record MessageTemplate(string Subject, string Text, string Html);
}
