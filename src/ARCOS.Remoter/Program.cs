using System.Text;
using ARCOS.Core.Drivers;
using ARCOS.Core.Engines;

namespace ARCOS.Remoter;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("==================================================");
        Console.WriteLine("   ARCOS Remoter Sidecar Companion (.NET 8 C#)   ");
        Console.WriteLine("==================================================");

        var io = new IoEngine();
        var varEng = new VariableEngine();
        var pt = new PointEngine(io);
        var img = new ImageEngine(io);
        var daq = new DaqEngine(io);
        var timer = new TimerEngine(io);

        var engines = new BasicEngines(io, varEng, pt, img, daq, timer);
        var drivers = new DriverManager();

        int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 2800;
        var server = new TelnetServer(port, engines, drivers);
        server.Start();

        Console.WriteLine("Press Ctrl+C to stop...");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            server.Stop();
            tcs.TrySetResult();
        };

        await tcs.Task;
    }
}
