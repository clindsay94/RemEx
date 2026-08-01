using Xunit;

// Three classes in this assembly drive RemexDesktopClient.Current — a PROCESS SINGLETON with a
// private constructor, so they cannot be given an instance of their own. DesktopConnectTimeoutTests
// additionally sets a static connect-timeout override for its duration.
//
// xUnit runs different test classes in parallel by default, and a per-class setUp/tearDown only
// prevents that override leaking AFTER the class finishes — not DURING, which is exactly the window
// another collection runs in. Today nothing breaks by luck: the other two classes return early on
// their own guards before touching a socket, so neither ever reads the timeout. One future test that
// genuinely connects and this fails nondeterministically, looking for all the world like a network
// problem rather than a test-isolation one.
//
// remex.agent.tests already disables parallelism for the same class of reason (shared singleton
// services inside the in-process host). This assembly runs in about two seconds, so the cost is
// nothing next to a flake that presents as a broken PC. (RemEx-g7hr)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
