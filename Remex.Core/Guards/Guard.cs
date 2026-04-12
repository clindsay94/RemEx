using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Remex.Core.Guards;

/// <summary>
/// Provides guard helper methods for null checking and validation.
/// Improves null safety by converting nullable references to non-null with runtime checks.
/// </summary>
public static class Guard
{
    /// <summary>
    /// Throws ArgumentNullException if argument is null.
    /// </summary>
    /// <typeparam name="T">The type of the argument</typeparam>
    /// <param name="argument">The argument to check</param>
    /// <param name="paramName">The parameter name (auto-captured)</param>
    /// <returns>The non-null value</returns>
    /// <exception cref="ArgumentNullException">Thrown when argument is null</exception>
    public static T NotNull<T>(
        [NotNull] T? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        where T : class
    {
        if (argument is null)
            throw new ArgumentNullException(paramName);
        return argument;
    }

    /// <summary>
    /// Throws ArgumentNullException if string is null or whitespace.
    /// </summary>
    /// <param name="argument">The string to check</param>
    /// <param name="paramName">The parameter name (auto-captured)</param>
    /// <returns>The non-null, non-empty string</returns>
    /// <exception cref="ArgumentNullException">Thrown when argument is null or whitespace</exception>
    public static string NotNullOrWhiteSpace(
        [NotNull] string? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(argument))
            throw new ArgumentNullException(paramName, "Value cannot be null or whitespace");
        return argument;
    }

    /// <summary>
    /// Throws ArgumentNullException if string is null or empty.
    /// </summary>
    /// <param name="argument">The string to check</param>
    /// <param name="paramName">The parameter name (auto-captured)</param>
    /// <returns>The non-null, non-empty string</returns>
    /// <exception cref="ArgumentNullException">Thrown when argument is null or empty</exception>
    public static string NotNullOrEmpty(
        [NotNull] string? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        if (string.IsNullOrEmpty(argument))
            throw new ArgumentNullException(paramName, "Value cannot be null or empty");
        return argument;
    }

    /// <summary>
    /// Throws InvalidOperationException if service is not registered in DI container.
    /// Use this after GetService&lt;T&gt;() calls to ensure required services are registered.
    /// </summary>
    /// <typeparam name="T">The service type</typeparam>
    /// <param name="service">The service instance from DI (may be null)</param>
    /// <param name="serviceName">The service name (auto-captured)</param>
    /// <returns>The non-null service instance</returns>
    /// <exception cref="InvalidOperationException">Thrown when service is not registered</exception>
    public static T RequiredService<T>(
        [NotNull] T? service,
        [CallerArgumentExpression(nameof(service))] string? serviceName = null)
        where T : class
    {
        if (service is null)
        {
            var typeName = typeof(T).Name;
            throw new InvalidOperationException(
                $"Required service {typeName} is not registered. " +
                $"Check dependency injection configuration in App.axaml.cs or Program.cs. " +
                $"Expression: {serviceName}");
        }
        return service;
    }

    /// <summary>
    /// Throws ArgumentOutOfRangeException if value is not in the specified range.
    /// </summary>
    /// <typeparam name="T">The comparable type</typeparam>
    /// <param name="value">The value to check</param>
    /// <param name="min">Minimum allowed value (inclusive)</param>
    /// <param name="max">Maximum allowed value (inclusive)</param>
    /// <param name="paramName">The parameter name (auto-captured)</param>
    /// <returns>The value if within range</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when value is outside range</exception>
    public static T InRange<T>(
        T value,
        T min,
        T max,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(min) < 0 || value.CompareTo(max) > 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"Value must be between {min} and {max}");
        }
        return value;
    }

    /// <summary>
    /// Throws ArgumentException if collection is null or empty.
    /// </summary>
    /// <typeparam name="T">The element type</typeparam>
    /// <param name="collection">The collection to check</param>
    /// <param name="paramName">The parameter name (auto-captured)</param>
    /// <returns>The non-null, non-empty collection</returns>
    /// <exception cref="ArgumentException">Thrown when collection is null or empty</exception>
    public static IEnumerable<T> NotNullOrEmpty<T>(
        [NotNull] IEnumerable<T>? collection,
        [CallerArgumentExpression(nameof(collection))] string? paramName = null)
    {
        if (collection is null || !collection.Any())
        {
            throw new ArgumentException("Collection cannot be null or empty", paramName);
        }
        return collection;
    }
}
