namespace Remex.Agent.Services.Session;

/// <summary>
/// The OS primitive behind <see cref="WindowsInteractiveSessionGuard"/>: takes a keep-awake hold
/// (no idle sleep, no display-off) and hands back an object whose <see cref="IDisposable.Dispose"/>
/// releases it.
/// </summary>
/// <remarks>
/// CONTRACT: the hold must NOT be thread-affine. The guard acquires it on whichever thread the first
/// client engages from and releases it on whichever thread the last client disengages from, and those
/// are routinely different thread-pool workers (the caller awaits between the two). That is exactly
/// what the old <c>SetThreadExecutionState</c> implementation got wrong - its state is per calling
/// thread, so a release from another thread cleared nothing and the hold outlived the session
/// (PERF-TRACKER P1-10). It exists as a seam so the guard's ref-counting can be tested without
/// touching real power state.
/// </remarks>
internal interface IKeepAwakeBackend
{
    /// <summary>Takes a keep-awake hold. Throws if it could not be taken.</summary>
    IDisposable Acquire();
}
