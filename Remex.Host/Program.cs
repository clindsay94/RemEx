using Remex.Host;

// --doctor / doctor: run the Linux prerequisite report (and optional safe repairs)
// instead of starting the WebApplication. Returns nonzero exit if the system
// cannot stream remote desktop. Linux-only; on other platforms it explains and
// exits early.
if (args.Length > 0 &&
    (args[0].Equals("--doctor", System.StringComparison.OrdinalIgnoreCase) ||
     args[0].Equals("doctor", System.StringComparison.OrdinalIgnoreCase)))
{
    if (System.OperatingSystem.IsLinux())
    {
        return await HostDoctor.RunAsync();
    }
    System.Console.Error.WriteLine("remex-host --doctor is only supported on Linux.");
    return 2;
}

var app = HostBootstrapper.CreateApplication(args);
app.Run();
return 0;

// Needed for WebApplicationFactory<Program> in integration tests.
public partial class Program { }
