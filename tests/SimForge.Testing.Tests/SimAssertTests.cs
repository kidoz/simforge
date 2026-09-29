using SimForge.Assertions;
using Xunit;

namespace SimForge.Testing.Tests;

public sealed class SimAssertTests
{
    [Fact]
    public void Passing_assertions_do_not_throw_and_return_useful_values()
    {
        SimAssert.True(true);
        SimAssert.False(false);
        SimAssert.Equal(3, 1 + 2);
        SimAssert.NotEqual("a", "b");
        SimAssert.Null(null);
        Assert.Equal("x", SimAssert.NotNull((string?)"x"));
        Assert.Equal(5, SimAssert.NotNull((int?)5));
        SimAssert.Empty(Array.Empty<int>());
        SimAssert.NotEmpty([1]);
        SimAssert.Count(2, [1, 2]);
        Assert.Equal(7, SimAssert.Single([7]));
        Assert.Equal(4, SimAssert.Single([3, 4, 5], value => value % 2 == 0));
        SimAssert.Contains(2, [1, 2, 3]);
        SimAssert.SequenceEqual([1, 2], new List<int> { 1, 2 });
        Assert.Equal("inner", SimAssert.Throws<InvalidOperationException>(() => throw new InvalidOperationException("inner")).Message);
    }

    [Fact]
    public void Failures_throw_simforge_assertion_exceptions_with_expected_actual_and_expression()
    {
        var actual = 41;

        var exception = Assert.Throws<SimForgeAssertionException>(() => SimAssert.Equal(42, actual, "answer mismatch"));

        Assert.Contains("SimAssert.Equal failed for `actual`.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("answer mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Expected: 42", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Actual:   41", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_failing_assertion_throws()
    {
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.True(false));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.False(true));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.NotEqual(1, 1));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.Null("value"));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.NotNull((string?)null));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.NotNull((int?)null));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.Empty([1]));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.NotEmpty(Array.Empty<int>()));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.Count(1, [1, 2]));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.Single([1, 2]));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.Single([1, 3], value => value % 2 == 0));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.Contains(9, [1, 2]));
        Assert.Throws<SimForgeAssertionException>(() => SimAssert.Fail("explicit"));
    }

    [Fact]
    public void Sequence_mismatch_reports_the_first_difference()
    {
        var exception = Assert.Throws<SimForgeAssertionException>(() => SimAssert.SequenceEqual([1, 2, 3], [1, 9, 3]));

        Assert.Contains("First difference at index 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throws_reports_missing_and_wrong_exceptions_and_preserves_the_wrong_one()
    {
        var none = Assert.Throws<SimForgeAssertionException>(() => SimAssert.Throws<InvalidOperationException>(() => { }));
        var wrong = Assert.Throws<SimForgeAssertionException>(() => SimAssert.Throws<InvalidOperationException>(() => throw new FormatException("bad")));
        var asyncWrong = await Assert.ThrowsAsync<SimForgeAssertionException>(
            () => SimAssert.ThrowsAsync<InvalidOperationException>(() => Task.FromException(new FormatException("bad async"))));

        Assert.Contains("no exception", none.Message, StringComparison.Ordinal);
        Assert.IsType<FormatException>(wrong.InnerException);
        Assert.IsType<FormatException>(asyncWrong.InnerException);
        Assert.IsType<ArgumentNullException>(await SimAssert.ThrowsAsync<ArgumentException>(() => Task.FromException(new ArgumentNullException("x"))));
    }

    [Fact]
    public void Long_strings_and_collections_are_truncated_in_messages()
    {
        var exception = Assert.Throws<SimForgeAssertionException>(() => SimAssert.Equal(new string('a', 500), "b"));
        var collection = Assert.Throws<SimForgeAssertionException>(() => SimAssert.Empty(Enumerable.Range(0, 50)));

        Assert.Contains("(500 chars)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("...]", collection.Message, StringComparison.Ordinal);
    }
}
