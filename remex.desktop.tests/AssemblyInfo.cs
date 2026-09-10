using Xunit;

// Two classes in this assembly mutate LocalizationService.Instance — a PROCESS SINGLETON whose
// culture every localized getter reads. FileTransferQueueTests switches to "fr" to prove queue
// labels re-render, and HostPlatformLabelTests switches to "hi" to prove the host summary puts its
// platform where the language wants it (RemEx-6s34).
//
// xUnit runs different test classes in parallel by default, and restoring the culture in a finally
// only prevents a leak AFTER the class finishes — not DURING, which is exactly the window the other
// collection runs in. This was not theoretical: with both classes present the pair failed about one
// run in eight, in BOTH directions. FileTransferQueueTests snapshots an error message and compares
// it against a freshly-read resource, so a concurrent switch makes those two different languages;
// and the culture-order assertion here reads a summary that the other class has just flipped to
// French. Worse, both capture the culture at entry, so interleaved restores can park the singleton
// in the wrong language for whichever class runs next — a failure that surfaces somewhere else
// entirely and reads like a bad translation rather than a test-isolation bug.
//
// Matches remex.core.tests and remex.agent.tests, which disable parallelization for the same reason
// against their own singletons. The cost is a slower suite; the alternative is a flake that gets
// investigated as a product defect.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
