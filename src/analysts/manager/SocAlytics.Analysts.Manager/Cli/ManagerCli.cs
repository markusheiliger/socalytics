using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.LocalControl;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.Cli;

internal static class ManagerCli
{
    public const int Success = 0;
    public const int Refused = 1;
    public const int Usage = 2;
    public const int Unavailable = 3;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && !args[0].StartsWith("--autostart", StringComparison.Ordinal);

    public static void AttachParentConsole()
    {
        if (OperatingSystem.IsWindows())
        {
            AttachConsole(ParentProcess);
        }
    }

    public static async Task<int> RunAsync(string[] args, string socketPath, TextWriter output, TextWriter error)
    {
        async Task<int> Send(string command, JsonObject? arguments, Func<JsonNode, string>? render = null, bool json = false)
        {
            try
            {
                var response = await LocalControlClient.SendAsync(
                    socketPath, new LocalControlRequest(LocalControlProtocol.Version, command, arguments), Timeout);
                if (!response.Ok)
                {
                    await error.WriteLineAsync($"refused: {response.Error?.Code}{(string.IsNullOrEmpty(response.Error?.Message) ? "" : " - " + response.Error!.Message)}");
                    if (!string.IsNullOrEmpty(response.Error?.Remediation))
                    {
                        await error.WriteLineAsync(response.Error!.Remediation);
                    }

                    return Refused;
                }

                var result = response.Result ?? new JsonObject();
                await output.WriteLineAsync(json || render is null
                    ? result.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
                    : render(result));
                return Success;
            }
            catch (LocalControlUnavailableException e)
            {
                await error.WriteLineAsync(e.Message);
                return Unavailable;
            }
        }

        var root = new RootCommand("SocAlytics Analyst Manager");

        var status = new Command("status", "Show the Manager status.");
        var statusJson = new Option<bool>("--json") { Description = "Print the status report as JSON." };
        status.Options.Add(statusJson);
        status.SetAction((r, _) => Send("status", null, StatusText, r.GetValue(statusJson)));
        root.Subcommands.Add(status);

        var register = new Command("register", "Start the registration.");
        var platform = new Option<string>("--platform") { Description = "Platform endpoint URL.", Required = true };
        var stamp = new Option<string?>("--stamp-id") { Description = "Expected stamp identifier." };
        var label = new Option<string?>("--host-label") { Description = "Host label." };
        var noWait = new Option<bool>("--no-wait") { Description = "Print the pairing code and return." };
        register.Options.Add(platform);
        register.Options.Add(stamp);
        register.Options.Add(label);
        register.Options.Add(noWait);
        register.SetAction(async (r, _) =>
        {
            var a = new JsonObject { ["platformEndpoint"] = r.GetValue(platform) };
            if (r.GetValue(stamp) is { } s) { a["stampId"] = s; }
            if (r.GetValue(label) is { } l)
            {
                if (HostLabel.TryNormalize(l, out var problem) is not { } normalized)
                {
                    await error.WriteLineAsync(problem);
                    return Usage;
                }

                a["hostLabel"] = normalized;
            }

            return await Send("register", a, RegisterText);
        });
        root.Subcommands.Add(register);

        var pause = new Command("pause", "Pause the analysis.");
        var forMinutes = new Option<int?>("--for") { Description = "Pause for MINUTES." };
        var until = new Option<string?>("--until") { Description = "Pause until an RFC 3339 time." };
        pause.Options.Add(forMinutes);
        pause.Options.Add(until);
        pause.SetAction(async (r, _) =>
        {
            var m = r.GetValue(forMinutes);
            var u = r.GetValue(until);
            var a = new JsonObject();
            if (m is not null && u is not null)
            {
                await error.WriteLineAsync("Use either --for or --until.");
                return Usage;
            }

            if (m is { } minutes)
            {
                a["durationMinutes"] = minutes;
            }
            else if (u is not null)
            {
                if (!DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                {
                    await error.WriteLineAsync("--until must be an RFC 3339 timestamp.");
                    return Usage;
                }

                a["until"] = at.ToString("O", CultureInfo.InvariantCulture);
            }

            return await Send("pause", a, Accepted);
        });
        root.Subcommands.Add(pause);

        foreach (var name in new[] { "resume", "exit" })
        {
            var c = new Command(name, $"Send {name}.");
            c.SetAction((_, _) => Send(name, null, Accepted));
            root.Subcommands.Add(c);
        }

        var unregister = new Command("unregister", "Unregister the Manager.");
        var discard = new Option<bool>("--discard-local") { Description = "Discard unusable local remnants (restore-failed only)." };
        unregister.Options.Add(discard);
        unregister.SetAction((r, _) => Send("unregister", new JsonObject { ["discardLocal"] = r.GetValue(discard) }, Accepted));
        root.Subcommands.Add(unregister);

        var autostart = new Command("autostart", "Show or change start at sign-in.");
        var toggle = new Argument<string?>("state") { Arity = ArgumentArity.ZeroOrOne, Description = "on or off." };
        toggle.AcceptOnlyFromAmong("on", "off");
        autostart.Arguments.Add(toggle);
        autostart.SetAction((r, _) =>
        {
            var a = new JsonObject();
            if (r.GetValue(toggle) is { } t)
            {
                a["enabled"] = t == "on";
            }

            return Send("autostart", a, n => $"autostart: {((bool?)n["enabled"] == true ? "on" : "off")}");
        });
        root.Subcommands.Add(autostart);

        var concurrency = new Command("concurrency", "Show the concurrency limit, or set the host-local limit.");
        var limit = new Argument<string?>("n") { Arity = ArgumentArity.ZeroOrOne, Description = "The new host-local limit (a positive integer)." };
        concurrency.Arguments.Add(limit);
        concurrency.SetAction(async (r, _) =>
        {
            var a = new JsonObject();
            if (r.GetValue(limit) is { } text)
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                {
                    await error.WriteLineAsync("The limit must be an integer of at least 1.");
                    return Usage;
                }

                a["hostLocal"] = n;
            }

            return await Send("concurrency", a, n => ConcurrencyText(n));
        });
        root.Subcommands.Add(concurrency);

        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var e in parse.Errors)
            {
                await error.WriteLineAsync(e.Message);
            }

            return Usage;
        }

        var code = await parse.InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return code;
    }

    private static string Accepted(JsonNode n) =>
        $"{(string?)n["command"]}: accepted (registration {(string?)n["registrationState"]}, operating {(string?)n["operatingState"] ?? "none"})";

    private static string RegisterText(JsonNode n) =>
        $"Open {(string?)n["verificationUri"]}{Environment.NewLine}Pairing code: {(string?)n["pairingCode"]}{Environment.NewLine}" +
        $"Device fingerprint: {(string?)n["deviceFingerprint"]}{Environment.NewLine}Expires: {(string?)n["expiresAt"]}";

    private static string ConcurrencyText(JsonNode n) =>
        $"Concurrency: effective {(int?)n["effective"]} (host-local {(int?)n["hostLocal"]}, stamp maximum {n["stampMaximum"]?.ToString() ?? "unknown"}, runtime maximum {n["runtimeMaximum"]?.ToString() ?? "unknown"})";

    private static string StatusText(JsonNode n)
    {
        var lines = new List<string>
        {
            $"Registration: {(string?)n["registrationState"]}",
            $"Operating: {(string?)n["operatingState"] ?? "none"}",
            $"Club: {(string?)n["clubName"] ?? "-"}",
            $"Stamp: {(string?)n["stampId"] ?? "-"}",
            $"Fingerprint: {(string?)n["deviceFingerprint"] ?? "-"}",
            $"Uptime: {(double?)n["uptimeSeconds"] ?? 0:F0}s",
        };
        if (n["pauseRemainingSeconds"] is { } rem)
        {
            lines.Add($"Pause remaining: {(double)rem:F0}s");
        }

        if (n["concurrency"] is { } c)
        {
            lines.Add(ConcurrencyText(c));
        }

        var attempts = n["activeAttempts"] as JsonArray ?? [];
        lines.Add($"Active attempts: {attempts.Count}");
        lines.AddRange(attempts.Select(a =>
            $"  {(string?)a?["logicalJobId"]} #{(int?)a?["attemptNumber"]} {(string?)a?["capability"]} {(string?)a?["executionState"]} " +
            $"started {(string?)a?["startedAt"]}, last heartbeat {(string?)a?["lastConfirmedHeartbeatAt"] ?? "-"}"));

        lines.AddRange(QueueText(n["queueDepth"]));
        var host = n["hostUtilization"];
        lines.Add(Diagnostics.HostUtilizationText.Cpu(host?["cpuPercent"] is { } cpu ? (decimal)cpu : null));
        lines.Add(Diagnostics.HostUtilizationText.Memory((long?)host?["memoryUsedBytes"], (long?)host?["memoryTotalBytes"]));

        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> QueueText(JsonNode? q)
    {
        switch ((string?)q?["state"])
        {
            case "available" or "stale":
                var age = Diagnostics.QueueDepthView.FormatAge((double?)q?["ageSeconds"] ?? 0);
                yield return (string?)q?["state"] == "stale"
                    ? $"Queue (approximate guidance, observed {age} ago, stale):"
                    : $"Queue (approximate guidance, observed {age} ago):";
                foreach (var c in q?["capabilities"] as JsonArray ?? [])
                {
                    yield return $"  {(string?)c?["id"]}: {(int?)c?["waiting"]}";
                }

                yield return $"  Total: {(int?)q?["total"]}";
                break;
            case "no-capability":
                yield return "Queue: no capability assigned";
                break;
            case "unavailable":
                yield return $"Queue: unavailable ({(string?)q?["reason"] ?? "unknown"})";
                break;
            case "not-yet-available":
                yield return "Queue: no information yet";
                break;
        }
    }

    private const int ParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
