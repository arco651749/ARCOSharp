using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ARCOS.Core.Engines;

/// <summary>
/// Redis 連線引擎與本機內建 redis-server.exe 生命週期管理器。
/// - 載入 REDIS 模組（System@IncludeModule::Redis）或首次呼叫 Redis 指令時，自動偵測本機 127.0.0.1:6379 是否已啟動；
///   若尚未啟動，則自動於背景無視窗啟動隨附於 MODULES/REDIS/server/ 的 redis-server.exe，並作為預設 Session ("default")。
/// - 透過 Redis@CreateSession :: session=Name {url=host:port[/db]} 可建立其他遠端或獨立 DB 的具名 Session，不覆蓋預設本機 Session。
/// - 採零外部相依之純原生 TCP RESP2 協定實作。
/// </summary>
public static class RedisEngine
{
    public const string DefaultSessionName = "default";
    public const string DefaultLocalHost = "127.0.0.1";
    public const int DefaultLocalPort = 6379;

    public sealed record RedisEndpointConfig(string Host, int Port, int Database, string? Password, string RawUrl);

    public sealed record RedisReply(char Kind, string? Value, List<RedisReply>? Array = null)
    {
        public bool IsError => Kind == '-';
        public bool IsNull => Kind == '0';
        public string ToDisplayString()
        {
            if (IsNull) return "";
            if (Kind == '*' && Array != null)
                return string.Join(", ", Array.Select(x => x.ToDisplayString()));
            return Value ?? "";
        }
    }

    private sealed class PooledSession : IDisposable
    {
        public readonly object SyncRoot = new();
        public RedisEndpointConfig Config;
        private TcpClient? _client;
        private BufferedStream? _reader;
        private NetworkStream? _stream;

        public PooledSession(RedisEndpointConfig config)
        {
            Config = config;
        }

        public void UpdateConfig(RedisEndpointConfig config)
        {
            lock (SyncRoot)
            {
                DisconnectLocked();
                Config = config;
            }
        }

        public RedisReply Execute(string[] args, int connectTimeoutMs = 500, int readTimeoutMs = 3000)
        {
            lock (SyncRoot)
            {
                Exception? lastEx = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        EnsureConnectedLocked(connectTimeoutMs, readTimeoutMs);
                        WriteRespArray(_stream!, args);
                        _stream!.Flush();
                        return ReadRespReply(_reader!);
                    }
                    catch (Exception ex) when (attempt == 0 && (ex is IOException || ex is SocketException || ex is ObjectDisposedException))
                    {
                        lastEx = ex;
                        DisconnectLocked();
                    }
                    catch (Exception ex)
                    {
                        DisconnectLocked();
                        return new RedisReply('-', ex.Message);
                    }
                }
                return new RedisReply('-', lastEx?.Message ?? "Redis connection failed");
            }
        }

        private void EnsureConnectedLocked(int connectTimeoutMs, int readTimeoutMs)
        {
            if (_client != null && _client.Connected && _stream != null && _reader != null)
            {
                _client.ReceiveTimeout = readTimeoutMs;
                _client.SendTimeout = Math.Max(1000, readTimeoutMs);
                return;
            }

            DisconnectLocked();
            var tcp = new TcpClient { NoDelay = true };
            using var cts = new CancellationTokenSource(connectTimeoutMs);
            tcp.ConnectAsync(Config.Host, Config.Port, cts.Token).AsTask().GetAwaiter().GetResult();
            tcp.ReceiveTimeout = readTimeoutMs;
            tcp.SendTimeout = Math.Max(1000, readTimeoutMs);

            var ns = tcp.GetStream();
            var bs = new BufferedStream(ns, 8192);

            _client = tcp;
            _stream = ns;
            _reader = bs;

            if (!string.IsNullOrEmpty(Config.Password))
            {
                WriteRespArray(_stream, new[] { "AUTH", Config.Password });
                _stream.Flush();
                var authRep = ReadRespReply(_reader);
                if (authRep.IsError)
                    throw new IOException($"Redis AUTH failed: {authRep.Value}");
            }

            if (Config.Database > 0)
            {
                WriteRespArray(_stream, new[] { "SELECT", Config.Database.ToString(CultureInfo.InvariantCulture) });
                _stream.Flush();
                var selRep = ReadRespReply(_reader);
                if (selRep.IsError)
                    throw new IOException($"Redis SELECT {Config.Database} failed: {selRep.Value}");
            }
        }

        private void DisconnectLocked()
        {
            try { _reader?.Dispose(); } catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }
            _reader = null;
            _stream = null;
            _client = null;
        }

        public void Dispose()
        {
            lock (SyncRoot) DisconnectLocked();
        }
    }

    private static readonly object _serverStartLock = new();
    private static readonly ConcurrentDictionary<string, PooledSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _subscribers = new(StringComparer.OrdinalIgnoreCase);
    private static Process? _managedServerProcess;

    static RedisEngine()
    {
        _sessions[DefaultSessionName] = new PooledSession(
            new RedisEndpointConfig(DefaultLocalHost, DefaultLocalPort, 0, null, $"{DefaultLocalHost}:{DefaultLocalPort}"));
    }

    /// <summary>
    /// 解析 Redis URL / Endpoint，支援：
    /// - 127.0.0.1:6379
    /// - 127.0.0.1:6379/1
    /// - redis://192.168.1.10:6379
    /// - redis://:password@192.168.1.10:6379/2
    /// - localhost
    /// </summary>
    public static RedisEndpointConfig ParseUrl(string? rawUrl)
    {
        string trimmed = (rawUrl ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return new RedisEndpointConfig(DefaultLocalHost, DefaultLocalPort, 0, null, $"{DefaultLocalHost}:{DefaultLocalPort}");

        string work = trimmed;
        if (work.StartsWith("redis://", StringComparison.OrdinalIgnoreCase))
            work = work.Substring("redis://".Length);
        else if (work.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
            work = work.Substring("rediss://".Length);

        string? password = null;
        int atIdx = work.LastIndexOf('@');
        if (atIdx >= 0)
        {
            string userInfo = work.Substring(0, atIdx);
            work = work.Substring(atIdx + 1);
            int colon = userInfo.IndexOf(':');
            password = colon >= 0 ? userInfo.Substring(colon + 1) : userInfo;
        }

        int db = 0;
        int slashIdx = work.IndexOf('/');
        if (slashIdx >= 0)
        {
            string dbPart = work.Substring(slashIdx + 1).Trim();
            work = work.Substring(0, slashIdx);
            if (int.TryParse(dbPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDb) && parsedDb >= 0)
                db = parsedDb;
        }

        string host = DefaultLocalHost;
        int port = DefaultLocalPort;
        int portColon = work.LastIndexOf(':');
        if (portColon > 0 && portColon < work.Length - 1)
        {
            host = work.Substring(0, portColon).Trim();
            if (int.TryParse(work.Substring(portColon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p > 0)
                port = p;
        }
        else if (!string.IsNullOrWhiteSpace(work))
        {
            host = work.Trim();
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            host = DefaultLocalHost;

        return new RedisEndpointConfig(host, port, db, password, trimmed);
    }

    /// <summary>
    /// 尋找隨附於 MODULES/REDIS/server/ 之本機 redis-server.exe 路徑
    /// </summary>
    public static string? FindLocalRedisServerExecutable(string? moduleDir = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(moduleDir))
        {
            candidates.Add(Path.Combine(moduleDir, "server", "redis-server.exe"));
            candidates.Add(Path.Combine(moduleDir, "redis-server.exe"));
        }

        string baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "MODULES", "REDIS", "server", "redis-server.exe"));
        candidates.Add(Path.Combine(baseDir, "MODULES", "REDIS", "redis-server.exe"));

        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            candidates.Add(Path.Combine(dir.FullName, "src", "MODULES", "REDIS", "server", "redis-server.exe"));
            candidates.Add(Path.Combine(dir.FullName, "dist", "ARCOS_Runtime", "MODULES", "REDIS", "server", "redis-server.exe"));
            candidates.Add(Path.Combine(dir.FullName, "MODULES", "REDIS", "server", "redis-server.exe"));
            candidates.Add(Path.Combine(dir.FullName, "REDIS", "redis-server.exe"));
            dir = dir.Parent;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 確認本機 127.0.0.1:6379 Redis 伺服器是否正在執行；若未啟動則自動於背景啟動隨附之 redis-server.exe，
    /// 並確保預設 Session ("default") 指向此本機 Redis。
    /// </summary>
    public static bool EnsureLocalServerStarted(string? moduleDir = null, Action<string>? log = null)
    {
        if (ProbePing(DefaultLocalHost, DefaultLocalPort, timeoutMs: 180))
        {
            EnsureDefaultSessionRegistered();
            return true;
        }

        lock (_serverStartLock)
        {
            if (ProbePing(DefaultLocalHost, DefaultLocalPort, timeoutMs: 180))
            {
                EnsureDefaultSessionRegistered();
                return true;
            }

            string? exePath = FindLocalRedisServerExecutable(moduleDir);
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                log?.Invoke("[Redis] Local redis-server.exe not found in MODULES/REDIS/server/.");
                return false;
            }

            string serverDir = Path.GetDirectoryName(exePath)!;
            string confPath = Path.Combine(serverDir, "redis.conf");
            string args = File.Exists(confPath)
                ? $"\"{confPath}\""
                : "--port 6379 --bind 127.0.0.1 --save \"\" --appendonly no";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    WorkingDirectory = serverDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                _managedServerProcess = Process.Start(psi);
                log?.Invoke($"[Redis] Auto-started bundled local redis-server.exe from '{serverDir}' (PID={_managedServerProcess?.Id}).");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[Redis] Failed to start bundled redis-server.exe: {ex.Message}");
                return false;
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                if (ProbePing(DefaultLocalHost, DefaultLocalPort, timeoutMs: 150))
                {
                    EnsureDefaultSessionRegistered();
                    return true;
                }
                Thread.Sleep(50);
            }

            log?.Invoke("[Redis] Local redis-server.exe launched but did not respond to PING within 3s.");
            return false;
        }
    }

    private static void EnsureDefaultSessionRegistered()
    {
        _sessions.GetOrAdd(DefaultSessionName, _ => new PooledSession(
            new RedisEndpointConfig(DefaultLocalHost, DefaultLocalPort, 0, null, $"{DefaultLocalHost}:{DefaultLocalPort}")));
    }

    public static bool ProbePing(string host, int port, int timeoutMs = 250)
    {
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            using var cts = new CancellationTokenSource(timeoutMs);
            tcp.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            tcp.ReceiveTimeout = timeoutMs;
            tcp.SendTimeout = timeoutMs;
            using var ns = tcp.GetStream();
            using var bs = new BufferedStream(ns, 1024);
            WriteRespArray(ns, new[] { "PING" });
            ns.Flush();
            var rep = ReadRespReply(bs);
            return !rep.IsError && string.Equals(rep.Value, "PONG", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 建立或更新具名 Redis Session（連線其他 Redis 伺服器或不同 DB；不覆蓋預設本機 Session）。
    /// </summary>
    public static bool CreateSession(string sessionName, string? url, out string statusMessage, string? moduleDir = null)
    {
        string name = string.IsNullOrWhiteSpace(sessionName) ? DefaultSessionName : sessionName.Trim();
        var cfg = ParseUrl(url);

        if (cfg.Host == DefaultLocalHost && cfg.Port == DefaultLocalPort)
        {
            EnsureLocalServerStarted(moduleDir);
        }

        var session = _sessions.AddOrUpdate(
            name,
            _ => new PooledSession(cfg),
            (_, existing) => { existing.UpdateConfig(cfg); return existing; });

        var ping = session.Execute(new[] { "PING" }, connectTimeoutMs: 500, readTimeoutMs: 1000);
        if (!ping.IsError && string.Equals(ping.Value, "PONG", StringComparison.OrdinalIgnoreCase))
        {
            statusMessage = "OK";
            return true;
        }

        statusMessage = $"ERR: {ping.Value ?? "Connection failed"}";
        return false;
    }

    public static string NormalizeSessionName(string? explicitSession)
    {
        return string.IsNullOrWhiteSpace(explicitSession) ? DefaultSessionName : explicitSession.Trim();
    }

    private static PooledSession GetSession(string? sessionName, string? moduleDir = null)
    {
        string name = NormalizeSessionName(sessionName);
        if (name.Equals(DefaultSessionName, StringComparison.OrdinalIgnoreCase))
        {
            EnsureLocalServerStarted(moduleDir);
        }

        if (_sessions.TryGetValue(name, out var existing))
        {
            if (existing.Config.Host == DefaultLocalHost && existing.Config.Port == DefaultLocalPort)
                EnsureLocalServerStarted(moduleDir);
            return existing;
        }

        // 未先 CreateSession 的具名 Session，若不存在則 fallback 指向本機預設 Redis
        EnsureLocalServerStarted(moduleDir);
        return _sessions[DefaultSessionName];
    }

    public static RedisReply Execute(string? sessionName, string[] args, int readTimeoutMs = 3000, string? moduleDir = null)
    {
        if (args == null || args.Length == 0)
            return new RedisReply('-', "Empty Redis command");
        var session = GetSession(sessionName, moduleDir);
        return session.Execute(args, connectTimeoutMs: 500, readTimeoutMs: readTimeoutMs);
    }

    public static bool Set(string? sessionName, string key, string value, out string? error)
    {
        var rep = Execute(sessionName, new[] { "SET", key, value });
        error = rep.IsError ? rep.Value : null;
        return !rep.IsError;
    }

    public static string Get(string? sessionName, string key, out bool found, out string? error)
    {
        var rep = Execute(sessionName, new[] { "GET", key });
        if (rep.IsError)
        {
            found = false;
            error = rep.Value;
            return "";
        }
        error = null;
        if (rep.IsNull)
        {
            found = false;
            return "";
        }
        found = true;
        return rep.Value ?? "";
    }

    public static bool LPush(string? sessionName, string key, string value, out string? error)
    {
        var rep = Execute(sessionName, new[] { "LPUSH", key, value });
        error = rep.IsError ? rep.Value : null;
        return !rep.IsError;
    }

    public static string? LPop(string? sessionName, string key, out string? error)
    {
        var rep = Execute(sessionName, new[] { "LPOP", key });
        if (rep.IsError)
        {
            error = rep.Value;
            return null;
        }
        error = null;
        return rep.IsNull ? null : (rep.Value ?? "");
    }

    public static string? BLPop(string? sessionName, string key, double timeoutSec, out string? error)
    {
        int sec = Math.Max(0, (int)Math.Ceiling(timeoutSec));
        int readTimeoutMs = sec == 0 ? Timeout.Infinite : (sec + 3) * 1000;
        var rep = Execute(sessionName, new[] { "BLPOP", key, sec.ToString(CultureInfo.InvariantCulture) }, readTimeoutMs: readTimeoutMs);
        if (rep.IsError)
        {
            error = rep.Value;
            return null;
        }
        error = null;
        if (rep.IsNull || rep.Array == null || rep.Array.Count < 2)
            return null;
        return rep.Array[1].Value ?? "";
    }

    public static long Publish(string? sessionName, string channel, string message, out string? error)
    {
        var rep = Execute(sessionName, new[] { "PUBLISH", channel, message });
        if (rep.IsError)
        {
            error = rep.Value;
            return 0;
        }
        error = null;
        return long.TryParse(rep.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;
    }

    /// <summary>
    /// 訂閱 Redis Pub/Sub 頻道：在背景啟動獨立長連線監聽，收到訊息時即時寫入回呼（例如更新 VariableEngine / IoEngine）
    /// </summary>
    public static bool Subscribe(string? sessionName, string channel, Action<string, string>? onMessage = null, Action<string>? log = null)
    {
        string name = NormalizeSessionName(sessionName);
        var session = GetSession(name);
        var cfg = session.Config;
        string subKey = $"{name}::{channel}";

        if (_subscribers.TryRemove(subKey, out var oldCts))
        {
            try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
        }

        var cts = new CancellationTokenSource();
        _subscribers[subKey] = cts;

        _ = Task.Run(() =>
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                tcp.ConnectAsync(cfg.Host, cfg.Port, cts.Token).AsTask().GetAwaiter().GetResult();
                using var ns = tcp.GetStream();
                using var bs = new BufferedStream(ns, 4096);

                if (!string.IsNullOrEmpty(cfg.Password))
                {
                    WriteRespArray(ns, new[] { "AUTH", cfg.Password });
                    ns.Flush();
                    ReadRespReply(bs);
                }

                WriteRespArray(ns, new[] { "SUBSCRIBE", channel });
                ns.Flush();
                // 讀取 subscribe 確認封包
                ReadRespReply(bs);

                while (!cts.IsCancellationRequested && tcp.Connected)
                {
                    var msg = ReadRespReply(bs);
                    if (msg.Kind == '*' && msg.Array != null && msg.Array.Count >= 3 &&
                        string.Equals(msg.Array[0].Value, "message", StringComparison.OrdinalIgnoreCase))
                    {
                        string ch = msg.Array[1].Value ?? channel;
                        string payload = msg.Array[2].Value ?? "";
                        onMessage?.Invoke(ch, payload);
                    }
                }
            }
            catch (Exception ex) when (!cts.IsCancellationRequested)
            {
                log?.Invoke($"[Redis][Session:{name}] Subscribe '{channel}' ended: {ex.Message}");
            }
        }, cts.Token);

        return true;
    }

    public static RedisReply ExecuteRawCommand(string? sessionName, string rawCommandLine)
    {
        var tokens = TokenizeCommandLine(rawCommandLine);
        if (tokens.Count == 0)
            return new RedisReply('-', "Empty Redis command");
        return Execute(sessionName, tokens.ToArray());
    }

    public static List<string> TokenizeCommandLine(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;
        var sb = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '"';
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < input.Length)
                {
                    sb.Append(input[++i]);
                }
                else if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"' || c == '\'')
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    #region RESP2 Wire Codec

    private static void WriteRespArray(Stream stream, string[] args)
    {
        var sb = new StringBuilder();
        sb.Append('*').Append(args.Length).Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);

        foreach (var arg in args)
        {
            byte[] payload = Encoding.UTF8.GetBytes(arg ?? "");
            byte[] lenHdr = Encoding.ASCII.GetBytes($"${payload.Length}\r\n");
            stream.Write(lenHdr, 0, lenHdr.Length);
            if (payload.Length > 0)
                stream.Write(payload, 0, payload.Length);
            stream.WriteByte((byte)'\r');
            stream.WriteByte((byte)'\n');
        }
    }

    private static RedisReply ReadRespReply(BufferedStream reader)
    {
        int prefix = reader.ReadByte();
        if (prefix < 0) throw new IOException("Redis server closed connection.");
        char kind = (char)prefix;

        switch (kind)
        {
            case '+':
            case '-':
            case ':':
                return new RedisReply(kind, ReadCrLfLine(reader));

            case '$':
            {
                string lenStr = ReadCrLfLine(reader);
                int len = int.Parse(lenStr, CultureInfo.InvariantCulture);
                if (len < 0) return new RedisReply('0', null);
                byte[] buf = new byte[len];
                ReadExact(reader, buf, len);
                // consume trailing \r\n
                reader.ReadByte();
                reader.ReadByte();
                return new RedisReply('$', Encoding.UTF8.GetString(buf));
            }

            case '*':
            {
                string countStr = ReadCrLfLine(reader);
                int count = int.Parse(countStr, CultureInfo.InvariantCulture);
                if (count < 0) return new RedisReply('0', null);
                var items = new List<RedisReply>(count);
                for (int i = 0; i < count; i++)
                    items.Add(ReadRespReply(reader));
                return new RedisReply('*', null, items);
            }

            default:
                throw new IOException($"Unexpected RESP prefix '{kind}' (0x{prefix:X2})");
        }
    }

    private static string ReadCrLfLine(BufferedStream reader)
    {
        var bytes = new List<byte>(64);
        while (true)
        {
            int b = reader.ReadByte();
            if (b < 0) throw new IOException("Unexpected EOF while reading RESP line.");
            if (b == '\r')
            {
                int next = reader.ReadByte();
                if (next == '\n') break;
                bytes.Add((byte)b);
                if (next >= 0) bytes.Add((byte)next);
            }
            else
            {
                bytes.Add((byte)b);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void ReadExact(BufferedStream reader, byte[] buffer, int length)
    {
        int offset = 0;
        while (offset < length)
        {
            int n = reader.Read(buffer, offset, length - offset);
            if (n <= 0) throw new IOException("Unexpected EOF while reading RESP bulk payload.");
            offset += n;
        }
    }

    #endregion
}
