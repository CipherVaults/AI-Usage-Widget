using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AIUsageWidget
{
    internal sealed class QuotaWindow
    {
        public string Label;
        public double Remaining;
        public DateTimeOffset? Reset;
        public string ResetDescription;
    }

    internal sealed class QuotaPool
    {
        public string Label;
        public readonly List<QuotaWindow> Windows = new List<QuotaWindow>();
    }

    internal sealed class QuotaReading
    {
        public List<QuotaPool> Pools;
        public DateTimeOffset Updated;
        public string Problem;
    }

    internal static partial class QuotaSources
    {
        private static readonly DateTimeOffset UnixEpoch =
            new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static string Profile { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        private static readonly string CodexHomeDirectory = Environment.GetEnvironmentVariable("CODEX_HOME");
        private static readonly string CodexHome = string.IsNullOrWhiteSpace(CodexHomeDirectory)
            ? Path.Combine(Profile, ".codex") : CodexHomeDirectory;
        private static readonly string AntigravityHome = Path.Combine(Profile, ".gemini", "antigravity");

        public static Task<QuotaReading> Codex()
        {
            return Task.Run(() => ReadSafely(ReadCodex, "Couldn’t reach Codex. Check your ChatGPT sign-in and try again."));
        }

        public static Task<QuotaReading> Antigravity()
        {
            return Task.Run(() => ReadSafely(ReadAntigravity,
                "Couldn’t read model quota. Check your Antigravity sign-in and try again."));
        }

        private static QuotaReading ReadSafely(Func<List<QuotaPool>> read, string message)
        {
            try
            {
                var pools = read();
                return pools.Count > 0
                    ? new QuotaReading { Pools = pools, Updated = DateTimeOffset.Now }
                    : new QuotaReading { Problem = message };
            }
            catch (QuotaSourceException exception)
            {
                return new QuotaReading { Problem = exception.Message };
            }
            catch (Exception)
            {
                // Vendor errors may contain account details. Only show our own messages.
                return new QuotaReading { Problem = message };
            }
        }

        private static List<QuotaPool> ReadCodex()
        {
            string binary = FindCodex();
            if (binary == null)
                throw new QuotaSourceException("Install the Codex app or CLI, then sign in with ChatGPT.");

            var settings = new ProcessStartInfo(binary, "app-server --listen stdio://")
            {
                WorkingDirectory = GetSafeWorkingDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            if (string.IsNullOrEmpty(settings.EnvironmentVariables["CODEX_HOME"]))
            {
                if (Directory.Exists(CodexHome))
                    settings.EnvironmentVariables["CODEX_HOME"] = CodexHome;
            }

            var json = new JavaScriptSerializer();
            using (var process = Process.Start(settings))
            {
                process.ErrorDataReceived += (sender, args) => { };
                process.BeginErrorReadLine();
                var stdout = new BoundedLineReader(process.StandardOutput);
                var timer = Stopwatch.StartNew();
                try
                {
                    var version = typeof(QuotaSources).Assembly.GetName().Version.ToString(3);
                    process.StandardInput.WriteLine("{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":" +
                        json.Serialize(new { name = "ai_usage_widget", title = "AI Usage Widget", version }) + "}}");
                    ReadReply(stdout, json, timer, 1);
                    process.StandardInput.WriteLine("{\"method\":\"initialized\"}");
                    process.StandardInput.WriteLine("{\"method\":\"account/rateLimits/read\",\"id\":2}");
                    return ParseCodex(ReadReply(stdout, json, timer, 2));
                }
                finally
                {
                    FinishChildProcess(process, 400);
                }
            }
        }

        private static string FindCodex()
        {
            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "OpenAI", "Codex", "bin", "codex.exe")
            };
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var path = directory.Trim().Trim('"');
                if (path.Length > 0 && Path.IsPathRooted(path))
                    candidates.Add(Path.Combine(path, "codex.exe"));
            }

            var binary = candidates.FirstOrDefault(File.Exists);
            if (binary != null)
                return binary;
            foreach (var process in Process.GetProcessesByName("codex"))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule.FileName;
                        if (File.Exists(path) && !IsReparsePoint(path) && HasValidAuthenticodeSignature(path))
                            binary = path;
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    catch (InvalidOperationException) { }
                }
                if (binary != null)
                    return binary;
            }
            return null;
        }

        private static object ReadReply(BoundedLineReader stdout, JavaScriptSerializer json, Stopwatch timer, int id)
        {
            for (var reply = ReadProcessMessage(stdout, json, timer); reply != null;
                reply = ReadProcessMessage(stdout, json, timer))
            {
                if (Number(Get(reply, "id")) != id)
                    continue;
                var error = Text(Get(Get(reply, "error"), "message"));
                if (error != null)
                {
                    if (error.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        error.IndexOf("credentials", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        error.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        error.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0)
                        throw new QuotaSourceException("Sign in to Codex with ChatGPT to see your plan quota.");
                    throw new IOException("Codex app-server request failed.");
                }
                return Get(reply, "result");
            }
            throw new IOException("Codex app server stopped.");
        }

        private static object ReadProcessMessage(BoundedLineReader stdout, JavaScriptSerializer json, Stopwatch timer)
        {
            const int timeoutMs = 12000;
            const int maxLineChars = 4 * 1024 * 1024;
            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                var line = stdout.ReadLine(timer, timeoutMs, maxLineChars);
                if (line == null)
                    return null;
                if (line.StartsWith("{", StringComparison.Ordinal))
                    return json.DeserializeObject(line);
            }
            throw new TimeoutException();
        }

        private static void FinishChildProcess(Process process, int gracePeriodMs)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            if (!process.WaitForExit(gracePeriodMs))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (NotSupportedException) { }
            }
        }

        internal static List<QuotaPool> ParseCodex(object result)
        {
            var pools = new List<QuotaPool>();
            var buckets = Get(result, "rateLimitsByLimitId") as Dictionary<string, object>;
            var primary = Get(buckets, "codex") ?? Get(result, "rateLimits");
            AddCodexPool(pools, primary, null);

            if (buckets != null)
            {
                var primaryId = Text(Get(primary, "limitId")) ?? "codex";
                foreach (var entry in buckets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (entry.Key != "codex" && entry.Key != primaryId)
                        AddCodexPool(pools, entry.Value, Text(Get(entry.Value, "limitName")) ?? entry.Key);
                }
            }
            return pools;
        }

        private static void AddCodexPool(List<QuotaPool> pools, object bucket, string label)
        {
            var pool = new QuotaPool { Label = label };
            foreach (var key in new[] { "primary", "secondary" })
            {
                var window = Get(bucket, key);
                var used = Number(Get(window, "usedPercent"));
                if (!used.HasValue)
                    continue;
                var duration = Number(Get(window, "windowDurationMins"));
                var reset = Number(Get(window, "resetsAt"));
                pool.Windows.Add(new QuotaWindow
                {
                    Label = WindowLabel(duration, key == "primary" ? "Primary" : "Secondary"),
                    Remaining = Percent(100 - used.Value),
                    Reset = reset > 0 && reset < 253402300799 ? (DateTimeOffset?)UnixEpoch.AddSeconds(reset.Value) : null
                });
            }
            if (pool.Windows.Count > 0)
                pools.Add(pool);
        }

        private static string WindowLabel(double? minutes, string fallback)
        {
            if (minutes == 10080) return "Weekly";
            if (minutes is double && minutes > 0)
            {
                if (minutes % 1440 == 0) return (minutes.Value / 1440).ToString(CultureInfo.InvariantCulture) + " days";
                if (minutes % 60 == 0) return (minutes.Value / 60).ToString(CultureInfo.InvariantCulture) + "h";
                return minutes.Value.ToString(CultureInfo.InvariantCulture) + " min";
            }
            return fallback;
        }

        private static List<QuotaPool> ReadAntigravity()
        {
            string directory = Path.Combine(AntigravityHome, "daemon");
            var listeners = GetLoopbackListeners();
            var json = new JavaScriptSerializer();
            bool serverFound = false;
            var files = Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("hub_*.json").OrderByDescending(file => file.LastWriteTimeUtc)
                : Enumerable.Empty<FileInfo>();

            foreach (var file in files)
            {
                try
                {
                    if (file.Length > 4096)
                        continue;
                    var hub = json.DeserializeObject(ReadBoundedFile(file.FullName, 4096, true));
                    var pidNumber = Number(Get(hub, "pid"));
                    var portNumber = Number(Get(hub, "httpPort"));
                    var token = Text(Get(hub, "csrfToken"));
                    if (!pidNumber.HasValue || !portNumber.HasValue || token == null ||
                        pidNumber.Value < 1 || pidNumber.Value > int.MaxValue || pidNumber.Value != Math.Floor(pidNumber.Value) ||
                        portNumber.Value < 1 || portNumber.Value > 65535 || portNumber.Value != Math.Floor(portNumber.Value))
                        continue;
                    int pid = (int)pidNumber.Value;
                    int port = (int)portNumber.Value;
                    if (!listeners.Contains(Tuple.Create(pid, port)) || !IsAntigravityProcess(pid))
                        continue;

                    serverFound = true;
                    var pools = ParseAntigravity(ReadAntigravitySummary(port, token, json));
                    if (pools.Count > 0)
                        return pools;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (WebException) { }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }

            if (!serverFound)
                throw new QuotaSourceException("Open the Antigravity desktop app and sign in to see its quota.");
            return new List<QuotaPool>();
        }

        private static object ReadAntigravitySummary(int port, string token, JavaScriptSerializer json)
        {
            const int maxResponseBytes = 2 * 1024 * 1024;
            // Use only the app's advertised loopback HTTP listener. Never follow redirects or use a proxy.
            var url = "http://127.0.0.1:" + port + "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.AllowAutoRedirect = false;
            request.Proxy = null;
            request.Timeout = 6500;
            request.ReadWriteTimeout = 6500;
            request.Headers["X-Codeium-Csrf-Token"] = token;
            request.Headers["Connect-Protocol-Version"] = "1";
            var body = Encoding.UTF8.GetBytes("{\"request\":{},\"forceRefresh\":true}");
            request.ContentLength = body.Length;
            using (var stream = request.GetRequestStream())
                stream.Write(body, 0, body.Length);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
                return json.DeserializeObject(ReadBoundedText(responseStream, maxResponseBytes));
        }

        internal static List<QuotaPool> ParseAntigravity(object result)
        {
            var pools = new List<QuotaPool>();
            var summary = Get(result, "response") ?? result;
            foreach (var group in Items(Get(summary, "groups")))
            {
                var pool = new QuotaPool { Label = Text(Get(group, "displayName")) ?? "Model pool" };
                foreach (var bucket in Items(Get(group, "buckets"))
                    .OrderBy(bucket => Text(Get(bucket, "window")) == "5h" ? 0 : 1))
                {
                    var fraction = Number(Get(bucket, "remainingFraction"));
                    var window = Text(Get(bucket, "window"));
                    var resetTime = Text(Get(bucket, "resetTime"));
                    // Proto JSON may omit a scalar zero and still report the reset time for that window.
                    if (!fraction.HasValue && resetTime == null)
                        continue;
                    DateTimeOffset reset;
                    pool.Windows.Add(new QuotaWindow
                    {
                        Label = window == "weekly" ? "Weekly" : window == "5h" ? "5h" :
                            Text(Get(bucket, "displayName")) ?? window ?? "Limit",
                        Remaining = Percent(100 * (fraction ?? 0)),
                        Reset = DateTimeOffset.TryParse(resetTime, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out reset) ? (DateTimeOffset?)reset : null
                    });
                }
                if (pool.Windows.Count > 0)
                    pools.Add(pool);
            }
            return pools;
        }

        private static bool IsAntigravityProcess(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    var path = process.MainModule.FileName;
                    if (string.IsNullOrEmpty(path) || IsReparsePoint(path))
                        return false;
                    var fullPath = Path.GetFullPath(path);
                    var tempRoot = Path.GetFullPath(Path.GetTempPath());
                    if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                        return false;
                    var bin = Path.GetDirectoryName(fullPath);
                    var resources = Directory.GetParent(bin);
                    var installation = resources == null ? null : resources.Parent;
                    if (!string.Equals(Path.GetFileNameWithoutExtension(fullPath), "language_server", StringComparison.OrdinalIgnoreCase)
                        || resources == null || !resources.Name.Equals("resources", StringComparison.OrdinalIgnoreCase)
                        || installation == null)
                        return false;
                    var appExe = Path.Combine(installation.FullName, "Antigravity.exe");
                    return File.Exists(appExe) && !IsReparsePoint(appExe) && new FileInfo(appExe).Length > 0;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort,
            int addressFamily, int tableClass, uint reserved);

        private static HashSet<Tuple<int, int>> GetLoopbackListeners()
        {
            const int ipv4 = 2;
            const int ownerPidListener = 3;
            const uint errorInsufficientBuffer = 122;
            const int maxTableBytes = 16 * 1024 * 1024;
            const int rowSize = 24;
            var listeners = new HashSet<Tuple<int, int>>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, ipv4, ownerPidListener, 0);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (size < 4 || size > maxTableBytes)
                    return listeners;
                var table = Marshal.AllocHGlobal(size);
                try
                {
                    uint status = GetExtendedTcpTable(table, ref size, false, ipv4, ownerPidListener, 0);
                    if (status == errorInsufficientBuffer)
                        continue;
                    if (status != 0)
                        return listeners;
                    int count = Marshal.ReadInt32(table);
                    if (count <= 0)
                        return listeners;
                    int maxRows = (size - 4) / rowSize;
                    if (count > maxRows)
                        count = maxRows;
                    for (int index = 0; index < count; index++)
                    {
                        // MIB_TCPROW_OWNER_PID has six DWORD fields. The port is in network byte order.
                        int row = 4 + index * rowSize;
                        if (Marshal.ReadInt32(table, row + 4) != 0x0100007f)
                            continue;
                        int port = Marshal.ReadByte(table, row + 8) * 256 + Marshal.ReadByte(table, row + 9);
                        int pid = Marshal.ReadInt32(table, row + 20);
                        listeners.Add(Tuple.Create(pid, port));
                    }
                    return listeners;
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            return listeners;
        }

        internal static bool IsReparsePoint(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    return false;
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch (ArgumentException) { return true; }
        }

        internal static string ReadBoundedFile(string path, int maxBytes, bool rejectReparsePoint)
        {
            if (rejectReparsePoint && IsReparsePoint(path))
                throw new IOException("Refusing to read reparse point.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.CanSeek && stream.Length > maxBytes)
                    throw new IOException("File exceeded size limit.");
                return ReadBoundedText(stream, maxBytes);
            }
        }

        internal static string ReadBoundedText(Stream stream, int maxBytes)
        {
            if (stream == null)
                throw new IOException("Empty stream.");
            var buffer = new byte[8192];
            using (var copy = new MemoryStream())
            {
                int total = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        throw new IOException("Stream exceeded size limit.");
                    copy.Write(buffer, 0, read);
                }
                copy.Position = 0;
                using (var reader = new StreamReader(copy, Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
        }

        private static string GetSafeWorkingDirectory()
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AIUsageWidget", "work");
                Directory.CreateDirectory(directory);
                if (!IsReparsePoint(directory))
                    return directory;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Environment.SystemDirectory;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

        internal static bool HasValidAuthenticodeSignature(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;
            var action = new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                pcwszFilePath = filePath
            };
            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            var data = new WinTrustData();
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
                data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = 2, // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1, // WTD_CHOICE_FILE
                    pFile = fileInfoPtr,
                    dwStateAction = 1, // WTD_STATEACTION_VERIFY
                    dwProvFlags = 0x00001010 // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
                };
                uint result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return result == 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            finally
            {
                if (data.hWVTStateData != IntPtr.Zero)
                {
                    data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                    WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                }
                Marshal.DestroyStructure(fileInfoPtr, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(fileInfoPtr);
            }
        }

        private sealed class BoundedLineReader
        {
            private readonly StreamReader reader;
            private readonly char[] chunk = new char[4096];
            private readonly StringBuilder pending = new StringBuilder();

            public BoundedLineReader(StreamReader reader)
            {
                this.reader = reader;
            }

            public string ReadLine(Stopwatch timer, int timeoutMs, int maxChars)
            {
                while (true)
                {
                    for (int i = 0; i < pending.Length; i++)
                    {
                        if (pending[i] == '\n')
                        {
                            int length = i > 0 && pending[i - 1] == '\r' ? i - 1 : i;
                            if (length > maxChars)
                            {
                                pending.Remove(0, i + 1);
                                throw new IOException("Child process line exceeded size limit.");
                            }
                            string line = pending.ToString(0, length);
                            pending.Remove(0, i + 1);
                            return line;
                        }
                    }
                    if (pending.Length > maxChars)
                        throw new IOException("Child process line exceeded size limit.");

                    long elapsed = timer.ElapsedMilliseconds;
                    if (elapsed >= timeoutMs)
                        throw new TimeoutException();
                    int remaining = (int)(timeoutMs - elapsed);

                    var readTask = reader.ReadAsync(chunk, 0, chunk.Length);
                    if (!readTask.Wait(remaining))
                        throw new TimeoutException();

                    int count = readTask.Result;
                    if (count == 0)
                    {
                        if (pending.Length == 0)
                            return null;
                        int length = pending[pending.Length - 1] == '\r' ? pending.Length - 1 : pending.Length;
                        if (length > maxChars)
                        {
                            pending.Clear();
                            throw new IOException("Child process line exceeded size limit.");
                        }
                        string tail = pending.ToString(0, length);
                        pending.Clear();
                        return tail;
                    }

                    int newlineIndex = Array.IndexOf(chunk, '\n', 0, count);
                    if (newlineIndex >= 0)
                    {
                        if (pending.Length + newlineIndex > maxChars)
                            throw new IOException("Child process line exceeded size limit.");
                        pending.Append(chunk, 0, count);
                    }
                    else
                    {
                        if (pending.Length + count > maxChars)
                            throw new IOException("Child process line exceeded size limit.");
                        pending.Append(chunk, 0, count);
                    }
                }
            }
        }

        private static object Get(object value, string key)
        {
            var map = value as IDictionary<string, object>;
            object found;
            return map != null && map.TryGetValue(key, out found) ? found : null;
        }

        private static IEnumerable<object> Items(object value)
        {
            return (value as object[]) ?? new object[0];
        }

        private static string Text(object value)
        {
            var text = value as string;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static double? Number(object value)
        {
            if (value == null || value is bool)
                return null;
            double number;
            return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                !double.IsNaN(number) && !double.IsInfinity(number) ? (double?)number : null;
        }

        private static double Percent(double value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private sealed class QuotaSourceException : Exception
        {
            public QuotaSourceException(string message) : base(message) { }
        }
    }
}
