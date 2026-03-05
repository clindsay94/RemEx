using Remex.Host;

var app = HostBootstrapper.CreateApplication(args);
app.Run();

// Needed for WebApplicationFactory<Program> in integration tests.
public partial class Program { }
