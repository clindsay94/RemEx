# Null Safety Guidelines for RemEx

This document establishes conventions for null safety using C# nullable reference types and runtime null guards.

---

## Why Null Safety Matters

1. **Prevent NullReferenceException**: The most common .NET exception
2. **Clear Error Messages**: Fail fast at service boundaries, not deep in call stacks
3. **Intent Documentation**: Explicitly show which dependencies are required vs optional
4. **Compiler Assistance**: Leverage C# nullable reference types for compile-time warnings

---

## Nullable Reference Types

All RemEx projects have nullable reference types enabled:

```xml
<PropertyGroup>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

This means:
- `string` is non-nullable (cannot be null)
- `string?` is nullable (may be null)
- Compiler warns when dereferencing potentially null values

---

## Guard Class Usage

The `Guard` class in `Remex.Core/Guards/Guard.cs` provides runtime null checking.

### Guard.NotNull&lt;T&gt;

Throws `ArgumentNullException` if argument is null:

```csharp
using Remex.Core.Guards;

public class MyService
{
    private readonly ILogger _logger;
    
    public MyService(ILogger logger)
    {
        _logger = Guard.NotNull(logger);
        // Compiler knows _logger is now definitely non-null
    }
}
```

**Benefits:**
- Clear error message shows parameter name
- Fails immediately at construction, not later during use
- Converts `T?` to `T` for null-flow analysis

### Guard.NotNullOrWhiteSpace

For string parameters that must have content:

```csharp
public class FileService
{
    private readonly string _filePath;
    
    public FileService(string filePath)
    {
        _filePath = Guard.NotNullOrWhiteSpace(filePath);
        // Ensures _filePath has actual content
    }
}
```

### Guard.NotNullOrEmpty

For strings that can be whitespace but not null/empty:

```csharp
public void ProcessData(string data)
{
    var validData = Guard.NotNullOrEmpty(data);
    // validData is guaranteed to have at least one character
}
```

### Guard.RequiredService&lt;T&gt;

For dependency injection scenarios:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Guards;

var services = BuildServiceProvider();

// ❌ BAD: GetService returns T? - may be null
var logger = services.GetService<ILogger>();
logger.LogInformation("Started"); // ⚠️ Potential NullReferenceException

// ✅ GOOD: GetRequiredService throws if not registered
var logger = services.GetRequiredService<ILogger>();
logger.LogInformation("Started"); // ✓ Always safe

// ✅ ALTERNATIVE: Guard for older code
var logger = Guard.RequiredService(services.GetService<ILogger>());
logger.LogInformation("Started"); // ✓ Always safe
```

---

## ViewModel Constructor Patterns

### Required Dependencies

Mark parameters as non-nullable and guard them:

```csharp
public partial class ShellViewModel : ObservableObject
{
    private readonly DashboardLayoutService _layoutService;
    private readonly ThemeService _themeService;
    
    public ShellViewModel(
        DashboardLayoutService layoutService,
        ThemeService themeService)
    {
        _layoutService = Guard.NotNull(layoutService);
        _themeService = Guard.NotNull(themeService);
    }
}
```

### Optional Dependencies

Mark parameters as nullable with default value:

```csharp
public partial class ShellViewModel : ObservableObject
{
    private readonly IImmersiveModeService? _immersiveMode;
    
    public ShellViewModel(
        DashboardLayoutService layoutService,
        IImmersiveModeService? immersiveMode = null)
    {
        _layoutService = Guard.NotNull(layoutService);
        _immersiveMode = immersiveMode; // Keep nullable
    }
    
    public void EnterImmersiveMode()
    {
        // Always check before use
        if (_immersiveMode is not null)
        {
            _immersiveMode.Enter();
        }
    }
}
```

---

## Service Resolution Patterns

### In App.axaml.cs

Use `GetRequiredService<T>()` for all required services:

```csharp
var services = collection.BuildServiceProvider();

// ✅ Required services - throw if not registered
var layoutService = services.GetRequiredService<DashboardLayoutService>();
var themeService = services.GetRequiredService<ThemeService>();
var shellVm = services.GetRequiredService<ShellViewModel>();
```

### Optional Service Resolution

Use `GetService<T>()` with null checks for truly optional services:

```csharp
// ✅ Optional service - check before use
var customService = services.GetService<ICustomService>();
if (customService is not null)
{
    customService.DoWork();
}
```

### Platform-Specific Services

```csharp
// ✅ Platform-specific service (may not exist on all platforms)
if (services.GetService<IImmersiveModeService>() is { } immersiveMode)
{
    immersiveMode.Enable();
}
```

---

## Property Nullability

### Observable Properties

```csharp
public partial class MyViewModel : ObservableObject
{
    // Non-nullable - must be initialized
    [ObservableProperty]
    private string _statusText = "Ready";
    
    // Nullable - may be null
    [ObservableProperty]
    private TelemetryPayload? _telemetry;
    
    // Non-nullable reference type - must be initialized
    [ObservableProperty]
    private ObservableCollection<string> _items = new();
}
```

### Nullable to Non-Nullable Conversion

When receiving nullable input but storing as non-nullable:

```csharp
public void SetHost(string? hostAddress)
{
    // Guard ensures non-null assignment
    HostAddress = Guard.NotNullOrWhiteSpace(hostAddress);
}
```

---

## Method Return Types

### Returning Nullable Results

```csharp
// Explicitly show method may return null
public TelemetryPayload? GetLatestTelemetry()
{
    return _latestTelemetry; // May be null
}
```

### Returning Non-Nullable Results

```csharp
// Always returns a value
public DashboardProfile GetProfile()
{
    return _profile ?? new DashboardProfile(); // Never null
}
```

### Task-Returning Methods

```csharp
// ❌ NEVER return null from a Task method
public async Task<string> LoadDataAsync()
{
    return null; // ⚠️ Compiler warning - returns Task<string>, not Task<string?>
}

// ✅ Return default value or throw exception
public async Task<string> LoadDataAsync()
{
    var data = await ReadAsync();
    return data ?? string.Empty; // ✓ Never null
}

// ✅ OR mark return type as nullable
public async Task<string?> LoadDataAsync()
{
    var data = await ReadAsync();
    return data; // ✓ Explicitly nullable
}
```

---

## Null-Coalescing Patterns

### Null Coalescing Operator (??)

```csharp
// Provide default value if null
var hostAddress = profile.HostAddress ?? "ws://localhost:3000/remex";

// Chain multiple null checks
var displayName = user.DisplayName ?? user.UserName ?? "Guest";
```

### Null-Conditional Operator (?.)

```csharp
// Safe navigation - returns null if any part is null
var length = user?.Name?.Length;

// Combine with null coalescing
var length = user?.Name?.Length ?? 0;
```

### Null-Coalescing Assignment (??=)

```csharp
// Assign only if null
_telemetry ??= new TelemetryPayload();

// Equivalent to:
if (_telemetry is null)
    _telemetry = new TelemetryPayload();
```

---

## Pattern Matching

### Null Checks with Pattern Matching

```csharp
// ✅ Modern pattern matching
if (telemetry is not null)
{
    ProcessTelemetry(telemetry);
}

// ✅ Pattern matching with property check
if (services.GetService<ThemeService>() is { } themeService)
{
    themeService.ApplyTheme();
}

// ✅ Property pattern
if (profile is { HostAddress: { } address })
{
    Connect(address); // address is non-null here
}
```

---

## Best Practices

### ✅ DO:

1. **Use Guard.NotNull** in constructors for required dependencies
2. **Use GetRequiredService** for services that must exist
3. **Mark optional parameters with `?` and default value**
4. **Check nullable fields before use** with `is not null` or `?.`
5. **Provide default values** instead of returning null when possible
6. **Fail fast** at boundaries (constructors, entry points)
7. **Document nullability** in XML comments when intent isn't obvious

```csharp
/// <summary>
/// Gets the current telemetry data, or null if not available.
/// </summary>
public TelemetryPayload? CurrentTelemetry => _telemetry;
```

### ❌ DON'T:

1. **Don't use `default!` to suppress warnings** unless absolutely necessary
2. **Don't use null-forgiving operator `!`** unless you can prove non-null
3. **Don't ignore nullable warnings** - fix them properly
4. **Don't make everything nullable** - use nullable only when truly optional
5. **Don't check for null after Guard.NotNull** - it's redundant
6. **Don't catch NullReferenceException** - prevent them with guards

---

## Null-Forgiving Operator (!)

Use **sparingly** and only when you know better than the compiler:

```csharp
// ❌ AVOID: Suppressing legitimate warning
public void Process(string? input)
{
    var length = input!.Length; // Danger! May throw if input is null
}

// ✅ EXCEPTION: When provably non-null after check
public void Process(string? input)
{
    if (string.IsNullOrEmpty(input))
        return;
    
    var length = input!.Length; // OK: we checked first
    
    // Better: use pattern matching to avoid ! entirely
    // if (input is { } nonNullInput)
    //     var length = nonNullInput.Length;
}

// ✅ VALID: Platform initialization guarantees non-null
public static IServiceProvider Services { get; private set; } = null!;
// Assigned in OnFrameworkInitializationCompleted before any access
```

---

## Compiler Warnings

### Enable Stricter Warnings

Consider enabling these in `Directory.Build.props`:

```xml
<PropertyGroup>
  <!-- Warn on nullable reference type mismatches -->
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  
  <!-- Specific nullable warnings -->
  <WarningsAsErrors>CS8600;CS8602;CS8603</WarningsAsErrors>
  
  <!-- CS8600: Converting null literal or possible null value to non-nullable type -->
  <!-- CS8602: Dereference of a possibly null reference -->
  <!-- CS8603: Possible null reference return -->
</PropertyGroup>
```

### Common Nullable Warnings

| Code | Meaning | Fix |
|:-----|:--------|:----|
| CS8600 | Assigning nullable to non-nullable | Add null check or use `Guard.NotNull` |
| CS8602 | Dereferencing possible null | Check with `is not null` or use `?.` |
| CS8603 | Returning possible null from non-nullable method | Return default value or change return type to `T?` |
| CS8618 | Non-nullable field not initialized | Initialize in constructor or at declaration |
| CS8625 | Cannot convert null literal to non-nullable | Use default value or make type nullable |

---

## Testing Null Safety

### Unit Test Examples

```csharp
[Fact]
public void Constructor_NullLogger_ThrowsArgumentNullException()
{
    // Arrange & Act & Assert
    var ex = Assert.Throws<ArgumentNullException>(() => 
        new MyService(null!));
    
    Assert.Contains("logger", ex.Message);
}

[Fact]
public void RequiredService_ServiceNotRegistered_ThrowsInvalidOperationException()
{
    // Arrange
    var services = new ServiceCollection().BuildServiceProvider();
    
    // Act & Assert
    var ex = Assert.Throws<InvalidOperationException>(() =>
        Guard.RequiredService(services.GetService<IMyService>()));
    
    Assert.Contains("not registered", ex.Message);
}
```

---

## Migration Guide

### Updating Existing Code

1. **Identify nullable dependencies**:
   ```csharp
   // OLD
   private readonly ILogger _logger;
   public MyClass(ILogger logger) => _logger = logger;
   
   // NEW
   public MyClass(ILogger logger) => _logger = Guard.NotNull(logger);
   ```

2. **Update service resolution**:
   ```csharp
   // OLD
   var service = Services.GetService<MyService>();
   service.DoWork(); // Potential null reference!
   
   // NEW
   var service = Services.GetRequiredService<MyService>();
   service.DoWork(); // Always safe
   ```

3. **Fix property warnings**:
   ```csharp
   // OLD
   public string StatusText { get; set; } // CS8618 warning
   
   // NEW - Option 1: Initialize
   public string StatusText { get; set; } = "Ready";
   
   // NEW - Option 2: Make nullable
   public string? StatusText { get; set; }
   ```

---

## Reference

- [C# Nullable Reference Types](https://learn.microsoft.com/en-us/dotnet/csharp/nullable-references)
- [Guard Clauses Pattern](https://ardalis.com/guard-clauses/)
- [Null Safety in .NET](https://learn.microsoft.com/en-us/dotnet/csharp/tutorials/nullable-reference-types)
