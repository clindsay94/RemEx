# Async/Await Guidelines for RemEx

## ConfigureAwait Policy

**Do NOT use `ConfigureAwait(false)` anywhere in this codebase.**

### Why?

1. **Avalonia UI**: Does not use `SynchronizationContext` for its dispatcher (unlike WPF). There is no context to avoid, so `ConfigureAwait(false)` has no effect.

2. **ASP.NET Core Host**: Removed `SynchronizationContext` in ASP.NET Core 2.0+. All requests run on thread pool threads without capturing context.

3. **Simplicity**: Removing `ConfigureAwait(false)` makes code cleaner and easier to read without sacrificing any functionality.

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
