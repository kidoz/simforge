using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace SimForge.Assertions;

/// <summary>
/// Runner-neutral assertions. Failures throw <see cref="SimForgeAssertionException"/>, which every runner adapter reports
/// as a failed test. Scenario bodies that use them run unchanged under any SimForge runner.
/// </summary>
public static class SimAssert
{
    private const int MaxCollectionItems = 10;
    private const int MaxStringLength = 200;

    public static void True([DoesNotReturnIf(false)] bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw Failure("True", expression, message, "Expected: true", "Actual:   false");
        }
    }

    public static void False([DoesNotReturnIf(true)] bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (condition)
        {
            throw Failure("False", expression, message, "Expected: false", "Actual:   true");
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null, [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw Failure("Equal", expression, message, $"Expected: {Format(expected)}", $"Actual:   {Format(actual)}");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual, string? message = null, [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw Failure("NotEqual", expression, message, $"Expected any value except: {Format(unexpected)}", $"Actual: {Format(actual)}");
        }
    }

    public static void Null(object? value, string? message = null, [CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (value is not null)
        {
            throw Failure("Null", expression, message, "Expected: null", $"Actual:   {Format(value)}");
        }
    }

    public static T NotNull<T>([NotNull] T? value, string? message = null, [CallerArgumentExpression(nameof(value))] string? expression = null)
        where T : class =>
        value ?? throw Failure("NotNull", expression, message, "Expected: a non-null value", "Actual:   null");

    public static T NotNull<T>([NotNull] T? value, string? message = null, [CallerArgumentExpression(nameof(value))] string? expression = null)
        where T : struct =>
        value ?? throw Failure("NotNull", expression, message, "Expected: a non-null value", "Actual:   null");

    public static void Empty<T>(IEnumerable<T> collection, string? message = null, [CallerArgumentExpression(nameof(collection))] string? expression = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var items = collection.ToList();
        if (items.Count != 0)
        {
            throw Failure("Empty", expression, message, "Expected: an empty collection", $"Actual:   {items.Count} item(s) {Format(items)}");
        }
    }

    public static void NotEmpty<T>(IEnumerable<T> collection, string? message = null, [CallerArgumentExpression(nameof(collection))] string? expression = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (!collection.Any())
        {
            throw Failure("NotEmpty", expression, message, "Expected: at least one item", "Actual:   an empty collection");
        }
    }

    public static void Count<T>(int expected, IEnumerable<T> collection, string? message = null, [CallerArgumentExpression(nameof(collection))] string? expression = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var items = collection.ToList();
        if (items.Count != expected)
        {
            throw Failure("Count", expression, message, $"Expected: {expected} item(s)", $"Actual:   {items.Count} item(s) {Format(items)}");
        }
    }

    public static T Single<T>(IEnumerable<T> collection, string? message = null, [CallerArgumentExpression(nameof(collection))] string? expression = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var items = collection.Take(2).ToList();
        return items.Count == 1
            ? items[0]
            : throw Failure("Single", expression, message, "Expected: exactly one item", $"Actual:   {Format(collection.ToList())}");
    }

    public static T Single<T>(IEnumerable<T> collection, Func<T, bool> predicate, string? message = null, [CallerArgumentExpression(nameof(collection))] string? expression = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(predicate);
        var matches = collection.Where(predicate).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw Failure("Single", expression, message, "Expected: exactly one matching item", $"Actual:   {matches.Count} matching item(s) {Format(matches)}");
    }

    public static void Contains<T>(T expected, IEnumerable<T> collection, string? message = null, [CallerArgumentExpression(nameof(collection))] string? expression = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var items = collection.ToList();
        if (!items.Contains(expected, EqualityComparer<T>.Default))
        {
            throw Failure("Contains", expression, message, $"Expected to contain: {Format(expected)}", $"Actual: {Format(items)}");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? message = null, [CallerArgumentExpression(nameof(actual))] string? expression = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var expectedItems = expected.ToList();
        var actualItems = actual.ToList();
        if (!expectedItems.SequenceEqual(actualItems, EqualityComparer<T>.Default))
        {
            var firstDifference = Enumerable.Range(0, Math.Max(expectedItems.Count, actualItems.Count))
                .First(index => index >= expectedItems.Count || index >= actualItems.Count ||
                                !EqualityComparer<T>.Default.Equals(expectedItems[index], actualItems[index]));
            throw Failure(
                "SequenceEqual",
                expression,
                message,
                $"Expected: {Format(expectedItems)}",
                $"Actual:   {Format(actualItems)}",
                $"First difference at index {firstDifference}");
        }
    }

    /// <summary>Asserts that <paramref name="action"/> throws <typeparamref name="TException"/> or a derived type, and returns the exception.</summary>
    public static TException Throws<TException>(Action action, string? message = null, [CallerArgumentExpression(nameof(action))] string? expression = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw Failure("Throws", expression, message, exception, $"Expected: {typeof(TException).FullName} (or derived)", $"Actual:   {exception.GetType().FullName}: {exception.Message}");
        }

        throw Failure("Throws", expression, message, $"Expected: {typeof(TException).FullName} (or derived)", "Actual:   no exception");
    }

    /// <summary>Asserts that the asynchronous <paramref name="action"/> throws <typeparamref name="TException"/> or a derived type, and returns the exception.</summary>
    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string? message = null, [CallerArgumentExpression(nameof(action))] string? expression = null)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw Failure("ThrowsAsync", expression, message, exception, $"Expected: {typeof(TException).FullName} (or derived)", $"Actual:   {exception.GetType().FullName}: {exception.Message}");
        }

        throw Failure("ThrowsAsync", expression, message, $"Expected: {typeof(TException).FullName} (or derived)", "Actual:   no exception");
    }

    [DoesNotReturn]
    public static void Fail(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        throw new SimForgeAssertionException(message);
    }

    private static SimForgeAssertionException Failure(string assertion, string? expression, string? message, params string[] lines) =>
        Failure(assertion, expression, message, innerException: null, lines);

    private static SimForgeAssertionException Failure(string assertion, string? expression, string? message, Exception? innerException, params string[] lines)
    {
        var builder = new StringBuilder("SimAssert.").Append(assertion).Append(" failed");
        if (!string.IsNullOrWhiteSpace(expression))
        {
            builder.Append(" for `").Append(expression).Append('`');
        }

        builder.Append('.');
        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.Append(' ').Append(message);
        }

        foreach (var line in lines)
        {
            builder.Append(Environment.NewLine).Append(line);
        }

        return new SimForgeAssertionException(builder.ToString(), innerException);
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => text.Length <= MaxStringLength ? $"\"{text}\"" : $"\"{text[..MaxStringLength]}\"... ({text.Length} chars)",
        char character => $"'{character}'",
        bool boolean => boolean ? "true" : "false",
        DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
        DateTime instant => instant.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable sequence => FormatSequence(sequence),
        _ => value.ToString() ?? value.GetType().Name,
    };

    private static string FormatSequence(IEnumerable sequence)
    {
        var items = sequence.Cast<object?>().Take(MaxCollectionItems + 1).ToList();
        var shown = string.Join(", ", items.Take(MaxCollectionItems).Select(Format));
        return items.Count > MaxCollectionItems ? $"[{shown}, ...]" : $"[{shown}]";
    }
}
