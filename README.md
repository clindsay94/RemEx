# Remex [Rem(ote)ex(ecution)]

Remex is a cross-platform remote PC management tool.

## Project Structure

- **Remex.Core**: The core library containing shared logic and interfaces.
- **Remex.Host**: A Worker Service project that runs the host application.
- **Remex.Client**: An Avalonia UI cross-platform client application.

## API Documentation

(API endpoints in Remex.Host will be documented here)

## Getting Started

### Prerequisites

- .NET 10 SDK

### Building

```bash
dotnet build
```

### Running

To run the host:
```bash
dotnet run --project Remex.Host
```

To run the client:
```bash
dotnet run --project Remex.Client
```
