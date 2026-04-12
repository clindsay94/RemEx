# Input Validation Guidelines for RemEx

This document establishes conventions for validating user input in ViewModels and services.

## Why Validate?

1. **Prevent Crashes**: Invalid input (malformed URIs, invalid MAC addresses) causes exceptions deep in the call stack
2. **Better UX**: Show friendly validation messages immediately, not after connection failures
3. **Security**: Defense against injection attacks and malformed data
4. **Data Integrity**: Ensure only valid data is persisted to configuration files

---

## Validation Architecture

### ObservableValidator Base Class

All ViewModels that accept user input should inherit from `ObservableValidator` instead of `ObservableObject`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using Remex.Core.Validation;

public partial class MyViewModel : ObservableValidator
{
    // Properties with validation
}
```

### NotifyDataErrorInfo Attribute

Add `[NotifyDataErrorInfo]` to properties that should trigger validation:

```csharp
[ObservableProperty]
[NotifyDataErrorInfo]
[Required(ErrorMessage = "This field is required")]
[ValidWebSocketUri]
private string _hostAddress = "";
```

This automatically:
- Validates the property when it changes
- Updates `HasErrors` property
- Triggers `ErrorsChanged` event
- Enables UI binding to validation errors

---

## Available Validation Attributes

### Built-in .NET Attributes

```csharp
using System.ComponentModel.DataAnnotations;

[Required(ErrorMessage = "Host address is required")]
[StringLength(255, MinimumLength = 1)]
[Range(1, 65535)]
[EmailAddress]
[Phone]
[Url]
```

### Custom RemEx Attributes

Defined in `Remex.Core/Validation/ValidationRules.cs`:

#### [ValidWebSocketUri]
Validates WebSocket URI format (ws:// or wss://)

```csharp
[ObservableProperty]
[NotifyDataErrorInfo]
[ValidWebSocketUri]
private string _hostAddress = "ws://localhost:3000/remex";
```

#### [ValidMacAddress]
Validates MAC address format (supports both : and - separators)

```csharp
[ObservableProperty]
[NotifyDataErrorInfo]
[ValidMacAddress]
private string _macAddress = "AA:BB:CC:DD:EE:FF";
```

#### [ValidIpAddress]
Validates IP address (IPv4 and IPv6)

```csharp
[ObservableProperty]
[NotifyDataErrorInfo]
[ValidIpAddress]
private string _broadcastIp = "255.255.255.255";
```

#### [ValidPort]
Validates port number (1-65535)

```csharp
[ObservableProperty]
[NotifyDataErrorInfo]
[ValidPort]
private int _port = 3000;
```

#### [ValidHostname]
Validates hostname (DNS name or IP address)

```csharp
[ObservableProperty]
[NotifyDataErrorInfo]
[ValidHostname]
private string _hostname = "example.com";
```

---

## Static Validation Helpers

For scenarios where attributes aren't suitable, use static helper methods from `NetworkValidation`:

```csharp
using Remex.Core.Validation;

if (!NetworkValidation.IsValidWebSocketUri(userInput, out string? errorMessage))
{
    StatusText = errorMessage;
    return;
}

if (!NetworkValidation.IsValidMacAddress(macAddress, out errorMessage))
{
    ShowError(errorMessage);
    return;
}
```

### Available Helper Methods:

- `NetworkValidation.IsValidWebSocketUri(string?, out string?)`
- `NetworkValidation.IsValidMacAddress(string?, out string?)`
- `NetworkValidation.IsValidIpAddress(string?, out string?)`
- `NetworkValidation.IsValidPort(int, out string?)`
- `NetworkValidation.IsValidHostname(string?, out string?)`
- `NetworkValidation.NormalizeMacAddress(string)` - Converts to standard format

---

## Validation Workflow

### 1. In ViewModel Constructor/Initialization

Properties marked with `[NotifyDataErrorInfo]` are validated automatically when set.

### 2. Before Critical Operations

Validate all properties before performing operations:

```csharp
[RelayCommand]
private async Task ConnectAsync()
{
    // Validate all properties
    ValidateAllProperties();
    
    // Check for errors
    if (HasErrors)
    {
        var errors = GetErrors(nameof(HostAddress))
            .Cast<string>()
            .FirstOrDefault();
        StatusText = errors ?? "Invalid connection settings";
        return;
    }
    
    // Proceed with connection
    await ActualConnectionLogic();
}
```

### 3. Validate Specific Property

```csharp
ValidateProperty(HostAddress, nameof(HostAddress));
if (HasErrors)
{
    // Handle error
}
```

---

## UI Binding to Validation Errors

### XAML Binding

Avalonia automatically shows validation errors with ErrorTemplate:

```xml
<TextBox Text="{Binding HostAddress}" 
         Watermark="ws://hostname:port/path"
         (DataValidationErrors.Errors)="{Binding (DataValidationErrors.Errors)}"/>
```

### Custom Error Display

```xml
<TextBlock Text="{Binding HostAddress.(DataValidationErrors.Errors)[0].ErrorContent}"
           IsVisible="{Binding HostAddress.(DataValidationErrors.HasErrors)}"
           Foreground="Red"/>
```

### Disable Button on Validation Errors

```csharp
private bool CanConnect() => !IsConnected && !IsConnecting && !HasErrors;

[RelayCommand(CanExecute = nameof(CanConnect))]
private async Task ConnectAsync() { }
```

The Connect button automatically disables when `HasErrors` is true.

---

## Best Practices

### ✅ DO:

1. **Validate at presentation layer** before passing to business logic
2. **Use descriptive error messages** that guide users to fix the issue
3. **Validate before critical operations** (network calls, file I/O, process execution)
4. **Use `ValidateAllProperties()` before commands** that depend on multiple inputs
5. **Provide default valid values** for properties when possible
6. **Normalize input** (e.g., MAC address format) before validation

### ❌ DON'T:

1. **Don't rely on exceptions** for validation — catch them at the boundary
2. **Don't validate too early** — wait for user to finish typing (use debouncing)
3. **Don't duplicate validation logic** — use centralized validation classes
4. **Don't show technical error messages** to users (expose friendly messages)
5. **Don't skip validation** in Command handlers — always validate before acting

---

## Examples

### Example 1: Connection Settings Validation

```csharp
public partial class ConnectionViewModel : ObservableValidator
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Host address is required")]
    [ValidWebSocketUri]
    private string _hostAddress = "ws://localhost:3000/remex";
    
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        ValidateAllProperties();
        if (HasErrors)
        {
            var errors = GetErrors(nameof(HostAddress))
                .Cast<string>()
                .FirstOrDefault();
            StatusText = errors ?? "Invalid settings";
            return;
        }
        
        // Proceed with connection
        await DoConnectionAsync();
    }
    
    private bool CanConnect() => !IsConnected && !HasErrors;
}
```

### Example 2: Wake-on-LAN Validation

```csharp
public partial class RemoteViewModel : ObservableValidator
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [ValidMacAddress]
    private string _wolMacAddress = "";
    
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [ValidIpAddress]
    private string _wolBroadcastIp = "255.255.255.255";
    
    [RelayCommand]
    private async Task SendWolAsync()
    {
        ValidateAllProperties();
        if (HasErrors)
        {
            WolStatusText = "Invalid MAC address or IP format";
            return;
        }
        
        // Normalize MAC address format
        var normalizedMac = NetworkValidation.NormalizeMacAddress(WolMacAddress);
        
        // Send WOL packet
        await _wolService.SendAsync(normalizedMac, WolBroadcastIp, WolPort);
    }
}
```

### Example 3: Settings Validation with Multiple Conditions

```csharp
public partial class SettingsViewModel : ObservableValidator
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 100, ErrorMessage = "Opacity must be between 1 and 100")]
    private int _windowOpacity = 95;
    
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 50, ErrorMessage = "Corner radius must be between 0 and 50")]
    private int _cornerRadius = 8;
    
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        ValidateAllProperties();
        if (HasErrors)
        {
            // Collect all error messages
            var allErrors = GetErrors()
                .SelectMany(e => e.Cast<string>())
                .ToList();
            
            StatusText = $"Validation failed: {string.Join(", ", allErrors)}";
            return;
        }
        
        await _layoutService.SaveAsync(BuildProfile());
        StatusText = "Settings saved successfully";
    }
}
```

---

## Testing Validation

### Unit Test Example

```csharp
[Fact]
public void HostAddress_InvalidUri_SetsValidationError()
{
    // Arrange
    var vm = new ConnectionViewModel();
    
    // Act
    vm.HostAddress = "not-a-valid-uri";
    
    // Assert
    Assert.True(vm.HasErrors);
    var errors = vm.GetErrors(nameof(vm.HostAddress)).Cast<string>();
    Assert.Contains("Invalid URI format", errors.First());
}

[Fact]
public void WolMacAddress_InvalidFormat_SetsValidationError()
{
    // Arrange
    var vm = new RemoteViewModel();
    
    // Act
    vm.WolMacAddress = "ZZ:ZZ:ZZ:ZZ:ZZ:ZZ";
    
    // Assert
    Assert.True(vm.HasErrors);
    var errors = vm.GetErrors(nameof(vm.WolMacAddress)).Cast<string>();
    Assert.Contains("Invalid MAC address", errors.First());
}
```

---

## Troubleshooting

### Validation Not Triggering

**Problem**: Property changes but validation doesn't run.

**Solution**: Ensure you added `[NotifyDataErrorInfo]` attribute:
```csharp
[ObservableProperty]
[NotifyDataErrorInfo]  // ← Required for automatic validation
[ValidWebSocketUri]
private string _hostAddress = "";
```

### Errors Not Clearing

**Problem**: Validation errors persist after fixing the input.

**Solution**: Validation re-runs automatically when property changes. If errors persist, check that the value is actually valid.

### Multiple Error Messages

**Problem**: Same validation error appears multiple times.

**Solution**: Ensure you're not validating the same property multiple times. Use `ValidateAllProperties()` once at the start of a command.

---

## References

- [CommunityToolkit.Mvvm Validation](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/observablevalidator)
- [Data Annotations](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.dataannotations)
- [Avalonia Data Validation](https://docs.avaloniaui.net/docs/data-binding/data-validation)
