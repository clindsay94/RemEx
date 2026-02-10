# Instructions for AI Agents

When working on this codebase, please adhere to the following guidelines:

1.  **Maintain cross-platform compatibility (Windows and Linux) at all times.**
    - Ensure code runs correctly on both operating systems.
    - Avoid platform-specific APIs unless wrapped in appropriate abstractions.

2.  **Use MVVM pattern for the Avalonia UI.**
    - Keep UI logic separate from business logic.
    - Utilize ViewModels and data binding.

3.  **All API endpoints in Remex.Host must be documented in the README.**
    - Whenever a new endpoint is added or modified, update the API Documentation section in `README.md`.

4.  **Prioritize .NET 10 performance features.**
    - Leverage new features and optimizations available in .NET 10 where applicable.

5.  **Ensure any changes are tested so that Linux compatibility isn't broken.**
    - Verify changes on Linux or write tests that can catch platform-specific issues.
