using Xunit;

// Several tests in this assembly (PairingHandlerTests, RemoteDesktopHandlerTests, etc.)
// share singleton services that live inside the in-process Kestrel-less WebApplicationFactory
// — chief among them PairingService, whose SemaphoreSlim and session state cannot tolerate
// concurrent access from two parallel test classes. xUnit's default behavior runs different
// test classes in parallel, which interleaves their server-side cleanup tasks against each
// other and against their own constructors, producing intermittent "pairing session already
// in progress" failures.
//
// The test suite runs in well under a second, so the cost of disabling test-level parallelism
// is negligible. Keeping it parallel was producing ~30% flake rate on PairingHandlerTests
// even with constructor-level state resets.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
