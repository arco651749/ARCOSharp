using System.Net;
using System.Net.Sockets;
using System.Text;
using MVCON.Core.Drivers;
using MVCON.Core.Execution;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;
using MVCON.Core.Model;

namespace MVCON.Remoter;

public sealed class TelnetServer
{
    private readonly int _port;
    private readonly BasicEngines _engines;
    private readonly DriverManager _drivers;
    private readonly CommandExecutor _executor;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public TelnetServer(int port, BasicEngines engines, DriverManager drivers)
    {
        _port = port;
        _engines = engines;
        _drivers = drivers;
        _executor = new CommandExecutor(ModuleRegistry.CreateDefault());
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"[Telnet Sidecar] Listening on TCP port {_port}...");
        _ = AcceptClientsAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        Console.WriteLine("[Telnet Sidecar] Stopped.");
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Telnet Sidecar] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
        {
            await writer.WriteLineAsync("200 MVCON Remoter Sidecar Ready (Port " + _port + ")").ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;

                line = line.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 Goodbye.").ConfigureAwait(false);
                    break;
                }

                string response = ProcessCommand(line);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
    }

    public string ProcessCommand(string line)
    {
        try
        {
            // 1. $GetIO <port>
            if (line.StartsWith("$GetIO", StringComparison.OrdinalIgnoreCase))
            {
                string port = line.Substring(6).Trim();
                bool val = _engines.Io.GetPort(port);
                return $"OK: {val}";
            }

            // 2. $SetIO <port>=<0|1|True|False>
            if (line.StartsWith("$SetIO", StringComparison.OrdinalIgnoreCase))
            {
                string rest = line.Substring(6).Trim();
                int eq = rest.IndexOf('=');
                if (eq > 0)
                {
                    string port = rest.Substring(0, eq).Trim();
                    string valStr = rest.Substring(eq + 1).Trim();
                    bool b = valStr.Equals("1") || valStr.Equals("True", StringComparison.OrdinalIgnoreCase) || valStr.Equals("On", StringComparison.OrdinalIgnoreCase);
                    _engines.Io.SetPort(port, b);
                    return "OK";
                }
                return "ERR: Invalid format, expected $SetIO Port=1";
            }

            // 3. $GetVariable <key> [section]
            if (line.StartsWith("$GetVariable", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Substring(12).Trim().Split(' ', StringSplitOptions.TrimEntries);
                string key = parts[0];
                string sec = parts.Length > 1 ? parts[1] : "Global";
                string val = _engines.Variable.Get(key, sec);
                return $"OK: {val}";
            }

            // 4. $SetVariable <key>=<value> [section]
            if (line.StartsWith("$SetVariable", StringComparison.OrdinalIgnoreCase))
            {
                string rest = line.Substring(12).Trim();
                int eq = rest.IndexOf('=');
                if (eq > 0)
                {
                    string key = rest.Substring(0, eq).Trim();
                    string right = rest.Substring(eq + 1).Trim();
                    string val = right;
                    string sec = "Global";

                    int sp = right.LastIndexOf(' ');
                    if (sp > 0)
                    {
                        val = right.Substring(0, sp).Trim();
                        sec = right.Substring(sp + 1).Trim();
                    }

                    _engines.Variable.Set(key, val, sec);
                    return "OK";
                }
                return "ERR: Invalid format, expected $SetVariable Key=Val";
            }

            // 5. $GetPoint <name>
            if (line.StartsWith("$GetPoint", StringComparison.OrdinalIgnoreCase))
            {
                string name = line.Substring(9).Trim();
                var pt = _engines.Point.GetPoint(name);
                return $"OK: ({pt.X},{pt.Y},{pt.Z},{pt.U},{pt.V},{pt.W})";
            }

            // 6. $SetPoint <name>=(X,Y,Z,U,V,W)
            if (line.StartsWith("$SetPoint", StringComparison.OrdinalIgnoreCase))
            {
                string rest = line.Substring(9).Trim();
                int eq = rest.IndexOf('=');
                if (eq > 0)
                {
                    string name = rest.Substring(0, eq).Trim();
                    string coords = rest.Substring(eq + 1).Trim().Trim('(', ')');
                    var tokens = coords.Split(',', StringSplitOptions.TrimEntries);
                    double ParseV(int i) => i < tokens.Length && double.TryParse(tokens[i], out double v) ? v : 0.0;
                    _engines.Point.SetPoint(name, new MachinePoint(ParseV(0), ParseV(1), ParseV(2), ParseV(3), ParseV(4), ParseV(5)));
                    return "OK";
                }
                return "ERR: Invalid format, expected $SetPoint Name=(X,Y,Z,U,V,W)";
            }

            // 7. $Execute <script> (單行分號串接腳本即時執行)
            if (line.StartsWith("$Execute", StringComparison.OrdinalIgnoreCase))
            {
                string script = line.Substring(8).Trim();
                var prog = ProgramTable.FromLines(script.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                var ctx = new ExecutionContext(999, "RemoteExec", prog, _engines, _drivers);

                while (ctx.IsRunning && ctx.ProgIndex <= prog.Count)
                {
                    var res = _executor.ExecuteStep(ctx);
                    if (res.Status == CommandStatus.Error)
                    {
                        return $"ERR: Command failed at line {ctx.FailedPC}: {res.ErrorMessage}";
                    }
                }
                return "OK: Executed";
            }

            return $"ERR: Unknown command '{line}'";
        }
        catch (Exception ex)
        {
            return $"ERR: Exception: {ex.Message}";
        }
    }
}
