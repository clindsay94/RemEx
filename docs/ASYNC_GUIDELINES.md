# Async/Await Guidelines for RemEx

## ConfigureAwait Policy

**Do NOT use `ConfigureAwait(false)` anywhere in this codebase.**

### Why?

1. **On the desktop side the captured context is often LOAD-BEARING, so the flag would be actively
   harmful.** `ConnectionViewModel.SendCommandAndWaitAsync` builds its `TaskCompletionSource` with
   `RunContinuationsAsynchronously` and it is completed by the receive loop, so the continuation is
   *guaranteed* not to run on the completing thread. What puts it back on the UI thread is the
   captured `AvaloniaSynchronizationContext`. Callers then assign `[ObservableProperty]` fields
   immediately after the await — `TaskManagerViewModel.KillError` is one. Add `ConfigureAwait(false)`
   there and a bound-property write moves to a thread-pool thread.

2. **On the host side there is no context, so there is nothing to gain either.** ASP.NET Core has run
   without a `SynchronizationContext` since 2.0, so on that side the flag genuinely is a no-op.

3. **One rule with no exceptions can be checked; a rule with judgement calls cannot.** Given 1 and 2,
   the flag ranges from harmful to pointless with nothing in between — but deciding which applies
   means knowing, at every await, whether the continuation touches bound state and whether the
   awaited task can complete off the UI thread. That is not a review anyone does reliably at speed,
   and a wrong call is invisible until it is not.

4. **A lone exception invites copies.** One surviving `ConfigureAwait(false)` reads as considered to
   the next person, which is exactly what happened (RemEx-8phl). `ConfigureAwaitBanTests` in
   `remex.core.tests` enforces this now, so the rule is not carried by prose alone.

### Correction (2026-08-01, RemEx-rbfq)

**This document previously said Avalonia "does not use `SynchronizationContext` for its dispatcher",
and concluded the flag "has no effect". That is wrong**, and it was repeated in two code comments.
Avalonia 11 installs `AvaloniaSynchronizationContext` on the UI thread via
`AvaloniaSynchronizationContext.Ensure(...)`, called from the dispatcher's main loop and from
`DispatcherOperation`, so it is in place for the life of the application, and nothing in
`Program.cs`'s `AppBuilder` disables it. (The type also exposes an `AutoInstall` / `InstallIfNeeded`
hook. That one belongs to the Designer and has no caller on the app path — citing it, as a draft of
this correction did, proves only that the type exists.) So on the desktop side the flag was never a
no-op; the ASP.NET Core half of the old claim was the only correct half.

**The rule did not change, only its justification.** Do not weaken the ban on the strength of this
correction: reasons 1 and 2 above hold regardless.

**Two tempting claims that are false. Both were drafted here and cut.**

*"The flag is unnecessary because nothing here blocks on a task."* There are blocking calls:
`RemexDesktopClient.Dispose` and several Linux host paths use `GetAwaiter().GetResult()`,
`PinnedCertStore` takes a semaphore synchronously, and the JNI pairing exports block by design
because a native export cannot be async. They sit where no context is captured — the Android JNI
layer and the ASP.NET Core host — which is why they are not deadlocks. But "no context is captured
*there*" is a different statement from "no context exists", and collapsing those two is precisely
how the original error in this document was made. RemEx-r9tv audits them.

*"Capturing the context buys nothing, so the flag is just noise."* Reason 1 above is the
counter-example, from this codebase: remove capture and a bound-property write leaves the UI thread.
The flag is not free to add. If you are checking this against a newer Avalonia,
inspect the shipped assembly rather than trusting this file — the previous version of this paragraph
is what happens otherwise. `ConfigureAwaitBanTests` in `remex.core.tests` now enforces the rule and
states the same reasoning at the point of failure.

### When ConfigureAwait IS needed (not applicable to RemEx):
- **WPF applications** with heavy UI thread marshalling
- **Windows Forms** applications
- **Legacy ASP.NET** (pre-Core) with request context

### Team Decision (2026-04-11):
After reviewing the codebase with the code review agent, we standardized on:
- **No `ConfigureAwait` anywhere**
- Analyzer rule CA2007 disabled in `.editorconfig`
- This document serves as the authoritative source of truth

## Best Practices

### DO:
- Use `await` directly on tasks: `await SomeMethodAsync();`
- Use async/await consistently throughout the call stack
- Return `Task` or `Task<T>` from async methods
- Use `ValueTask` for hot paths where performance matters
- Name async methods with the `Async` suffix

### DON'T:
- Don't use `.Result` or `.Wait()` (causes deadlocks)
- Don't use `async void` except for event handlers
- Don't return `null` from `Task`-returning methods (return `Task.CompletedTask`)
- Don't swallow exceptions in async methods
- Don't use `ConfigureAwait(false)` in this codebase

## Examples

### ✅ Correct
```csharp
public async Task<DashboardProfile> LoadAsync()
{
    await _gate.WaitAsync();
    try
    {
        var json = await File.ReadAllTextAsync(_filePath);
        return JsonSerializer.Deserialize<DashboardProfile>(json) ?? new DashboardProfile();
    }
    finally
    {
        _gate.Release();
    }
}
```

### ❌ Incorrect
```csharp
public async Task<DashboardProfile> LoadAsync()
{
    await _gate.WaitAsync().ConfigureAwait(false); // ❌ Don't use ConfigureAwait
    try
    {
        var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false); // ❌
        return JsonSerializer.Deserialize<DashboardProfile>(json) ?? new DashboardProfile();
    }
    finally
    {
        _gate.Release();
    }
}
```

## Avalonia Dispatcher Access

When you need to marshal back to the UI thread in Avalonia, use:

```csharp
await Dispatcher.UIThread.InvokeAsync(() => 
{
    // UI updates here
});
```

Or for fire-and-forget:

```csharp
Dispatcher.UIThread.Post(() => 
{
    // UI updates here
});
```

## References

- [Avalonia Threading Documentation](https://docs.avaloniaui.net/docs/concepts/reactiveui/threading)
- [ConfigureAwait FAQ by Stephen Toub](https://devblogs.microsoft.com/dotnet/configureawait-faq/)
- [ASP.NET Core SynchronizationContext](https://blog.stephencleary.com/2017/03/aspnetcore-synchronization-context.html)
