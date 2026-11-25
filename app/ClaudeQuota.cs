using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AIUsageWidget
{
    internal static partial class QuotaSources
    {
        private static readonly string ClaudeConfigDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        private static readonly string ClaudeHome = string.IsNullOrWhiteSpace(ClaudeConfigDirectory)
            ? Path.Combine(Profile, ".claude") : ClaudeConfigDirectory;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly Regex ClaudeUsageLine = new Regex(
            @"^\s*(?<window>Current session|Current week)(?:[ \t]+\((?<model>[^)\r\n]+)\))?[ \t]*:[ \t]*(?<used>\d+(?:[.,]\d+)?)[ \t]*%[ \t]+used\b[^\r\n]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline, RegexTimeout);
        private static readonly Regex ClaudeResets = new Regex(@"\bresets[ \t]+(?<when>[^\r\n]{1,120})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly Regex UtcZone = new Regex(@"[ \t]+\((?:UTC|Etc/UTC|GMT)\)\)?[ \t\.]*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        private static readonly string[] ClaudeResetTimeOnlyFormats =
        {
            "h:mmtt", "htt", "H:mm"
        };
        private static readonly string[] ClaudeResetFormats =
        {
            "MMM d 'at' h:mmtt", "MMM d 'at' htt", "MMM d 'at' H:mm",
            "MMM d, h:mmtt", "MMM d, htt", "MMM d, H:mm",
            "MMM d h:mmtt", "MMM d htt", "MMM d H:mm"
        };
        private static readonly string[] ClaudeResetWithYearFormats =
        {
            "MMM d, yyyy 'at' h:mmtt", "MMM d, yyyy 'at' htt", "MMM d, yyyy 'at' H:mm",
            "MMM d, yyyy, h:mmtt", "MMM d, yyyy, htt", "MMM d, yyyy, H:mm"
        };

        public static Task<QuotaReading> Claude()
        {
            return Task.Run(() =>
            {
                const string unavailable = "Claude Code didn’t return plan limits. Check /usage in Claude Code.";
                var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                string problem = unavailable;
                try
                {
                    var cli = FindClaude();
                    if (cli == null)
                        problem = "Install Claude Code for Windows to read your Claude plan limits.";
                    else
                    {
                        var output = ReadClaudeCommand(cli, json);
                        var pools = ParseClaudeUsageText(output);
                        if (pools.Count > 0)
                            return new QuotaReading { Pools = pools, Updated = DateTimeOffset.Now };
                        if (output != null && (output.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            output.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0))
                            problem = "Claude Code reports that it’s signed out. Run /login in Claude Code.";
                    }
                }
                catch (QuotaSourceException error) { problem = error.Message; }
                catch (TimeoutException) { problem = "The Claude Code quota check timed out. Try refreshing."; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (AggregateException) { }
                return ReadClaudeCache(json, problem);
            });
        }

        private static ProcessStartInfo FindClaude()
        {
            const string options = "--safe-mode --strict-mcp-config --no-session-persistence --input-format stream-json " +
                "--output-format stream-json --verbose --print";
            var directories = new[]
            {
                Path.Combine(Profile, ".local", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs")
            }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(entry => entry.Trim().Trim('"')).Where(entry => entry.Length > 0 && Path.IsPathRooted(entry)));

            string binary = directories.Select(directory => Path.Combine(directory, "claude.exe")).FirstOrDefault(File.Exists);
            string arguments = options;
            if (binary == null)
            {
                var script = directories.Select(directory =>
                    Path.Combine(directory, "node_modules", "@anthropic-ai", "claude-code", "cli.js")).FirstOrDefault(File.Exists);
                if (script == null || script.IndexOf('"') >= 0 || script.IndexOf('\0') >= 0)
                    return null;
                binary = directories.Select(directory => Path.Combine(directory, "node.exe")).FirstOrDefault(File.Exists);
                if (binary == null)
                    return null;
                arguments = "\"" + script + "\" " + options;
            }
            var settings = new ProcessStartInfo(binary, arguments)
            {
                WorkingDirectory = GetSafeWorkingDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            settings.EnvironmentVariables["TZ"] = "UTC";
            return settings;
        }

        private static string ReadClaudeCommand(ProcessStartInfo settings, JavaScriptSerializer json)
        {
            const string id = "ai-usage-widget";
            using (var process = Process.Start(settings))
            {
                try
                {
                    process.ErrorDataReceived += (sender, args) => { };
                    process.BeginErrorReadLine();
                    var stdout = new BoundedLineReader(process.StandardOutput);
                    var timer = Stopwatch.StartNew();
                    process.StandardInput.WriteLine("{\"type\":\"control_request\",\"request_id\":\"" + id +
                        "\",\"request\":{\"subtype\":\"initialize\"}}");

                    bool usageRequested = false;
                    for (var message = ReadProcessMessage(stdout, json, timer); message != null;
                        message = ReadProcessMessage(stdout, json, timer))
                    {
                        var response = Get(message, "response");
                        var type = Text(Get(message, "type"));
                        if (type == "control_response" && Text(Get(response, "request_id")) == id && !usageRequested)
                        {
                            var commandList = Get(Get(response, "response"), "commands");
                            // Submit only when the installed CLI advertises the local usage command.
                            if (!Items(commandList).Any(command =>
                                Text(command) == "usage" || Text(Get(command, "name")) == "usage"))
                                break;
                            usageRequested = true;
                            process.StandardInput.WriteLine("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"/usage\"},\"session_id\":\"\"}");
                            process.StandardInput.Close();
                        }
                        else if (type == "result" && usageRequested)
                        {
                            if (Number(Get(message, "num_turns")) != 0)
                                return null;
                            return Text(Get(message, "result"));
                        }
                    }
                    if (!usageRequested)
                        throw new QuotaSourceException("Claude Code didn’t offer /usage. Update Claude Code, then refresh.");
                    return null;
                }
                finally { FinishChildProcess(process, 2500); }
            }
        }

        internal static List<QuotaPool> ParseClaudeUsageText(string output)
        {
            var pools = new List<QuotaPool>();
            var main = new QuotaPool();
            foreach (Match match in ClaudeUsageLine.Matches(output ?? ""))
            {
                var used = Number(match.Groups["used"].Value.Replace(',', '.'));
                if (!used.HasValue)
                    continue;
                bool weekly = match.Groups["window"].Value.Equals("Current week", StringComparison.OrdinalIgnoreCase);
                var when = ClaudeResets.Match(match.Value);
                string resetText = when.Success ? when.Groups["when"].Value.Trim() : null;
                var reset = ParseClaudeReset(resetText);
                var window = new QuotaWindow
                {
                    Label = weekly ? "Weekly" : "5h",
                    Remaining = Percent(100 - used.Value),
                    Reset = reset,
                    ResetDescription = reset.HasValue || resetText == null ? null : "Resets " + resetText
                };
                var model = match.Groups["model"].Value.Trim();
                if (!weekly || model.Length == 0 || model.Equals("all models", StringComparison.OrdinalIgnoreCase))
                    main.Windows.Add(window);
                else
                {
                    var pool = new QuotaPool { Label = model };
                    pool.Windows.Add(window);
                    pools.Add(pool);
                }
            }
            if (main.Windows.Count > 0)
                pools.Insert(0, main);
            return pools;
        }

        private static DateTimeOffset? ParseClaudeReset(string value)
        {
            if (value == null)
                return null;
            var zone = UtcZone.Match(value);
            if (!zone.Success)
                return null;
            var timestamp = value.Substring(0, zone.Index);
            const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal |
                DateTimeStyles.AllowWhiteSpaces;
            DateTimeOffset reset;
            if (DateTimeOffset.TryParseExact(timestamp, ClaudeResetWithYearFormats, CultureInfo.InvariantCulture, styles, out reset))
                return reset;
            var now = DateTimeOffset.UtcNow;
            if (DateTimeOffset.TryParseExact(timestamp, ClaudeResetFormats, CultureInfo.InvariantCulture, styles, out reset))
            {
                if (reset < now.AddMonths(-6)) return reset.AddYears(1);
                if (reset > now.AddMonths(6)) return reset.AddYears(-1);
                return reset;
            }
            DateTime timeOnly;
            if (DateTime.TryParseExact(timestamp, ClaudeResetTimeOnlyFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out timeOnly))
            {
                var candidate = new DateTimeOffset(now.Year, now.Month, now.Day,
                    timeOnly.Hour, timeOnly.Minute, 0, TimeSpan.Zero);
                return candidate <= now ? candidate.AddDays(1) : candidate;
            }
            return null;
        }

        private static QuotaReading ReadClaudeCache(JavaScriptSerializer json, string problem)
        {
            const int maxCacheBytes = 16 * 1024 * 1024;
            var files = string.IsNullOrWhiteSpace(ClaudeConfigDirectory)
                ? new[] { Path.Combine(Profile, ".claude.json"), Path.Combine(ClaudeHome, ".claude.json") }
                : new[] { Path.Combine(ClaudeHome, ".claude.json") };
            foreach (var file in files)
            {
                try
                {
                    if (!File.Exists(file))
                        continue;
                    var config = json.DeserializeObject(ReadBoundedFile(file, maxCacheBytes, false));
                    var account = Text(Get(Get(config, "oauthAccount"), "accountUuid"));
                    var cache = Get(config, "cachedUsageUtilization");
                    var fetched = Number(Get(cache, "fetchedAtMs"));
                    if (account != null && account == Text(Get(cache, "accountUuid")) &&
                        fetched > 0 && fetched <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    {
                        var pools = ParseClaude(Get(cache, "utilization"));
                        if (pools.Count > 0)
                            return new QuotaReading { Pools = pools, Updated = UnixEpoch.AddMilliseconds(fetched.Value), Problem = problem };
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            return new QuotaReading { Problem = problem };
        }

        internal static List<QuotaPool> ParseClaude(object result)
        {
            QuotaWindow session = null;
            QuotaWindow weekly = null;
            var scoped = new Dictionary<string, QuotaWindow>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Items(Get(result, "limits")))
            {
                var kind = Text(Get(entry, "kind"));
                if (kind == "session")
                    session = ClaudeWindow("5h", entry, "percent");
                else if (kind == "weekly_all")
                    weekly = ClaudeWindow("Weekly", entry, "percent");
                else if (kind == "weekly_scoped")
                {
                    var model = Text(Get(Get(Get(entry, "scope"), "model"), "display_name"));
                    var window = ClaudeWindow("Weekly", entry, "percent");
                    if (model != null && window != null)
                        scoped[model] = window;
                }
            }
            session = session ?? ClaudeWindow("5h", Get(result, "five_hour"), "utilization");
            weekly = weekly ?? ClaudeWindow("Weekly", Get(result, "seven_day"), "utilization");
            foreach (var name in new[] { "Sonnet", "Opus", "Fable", "Cowork" })
            {
                var window = ClaudeWindow("Weekly", Get(result, "seven_day_" + name.ToLowerInvariant()), "utilization");
                if (window != null && !scoped.ContainsKey(name))
                    scoped.Add(name, window);
            }

            var pools = new List<QuotaPool>();
            var basePool = new QuotaPool();
            if (session != null) basePool.Windows.Add(session);
            if (weekly != null) basePool.Windows.Add(weekly);
            if (basePool.Windows.Count > 0) pools.Add(basePool);
            foreach (var model in scoped)
            {
                var pool = new QuotaPool { Label = model.Key };
                pool.Windows.Add(model.Value);
                pools.Add(pool);
            }
            return pools;
        }

        private static QuotaWindow ClaudeWindow(string label, object data, string usageKey)
        {
            var used = Number(Get(data, usageKey));
            if (!used.HasValue)
                return null;
            DateTimeOffset reset;
            return new QuotaWindow
            {
                Label = label,
                Remaining = Percent(100 - used.Value),
                Reset = DateTimeOffset.TryParse(Text(Get(data, "resets_at")), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out reset) ? (DateTimeOffset?)reset : null
            };
        }
    }
}
