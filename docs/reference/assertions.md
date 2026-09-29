# SimForge.Assertions

Namespace `SimForge.Assertions`. Static class `SimAssert`.

Every assertion throws `SimForgeAssertionException` on failure. The message names the assertion and the checked
expression (captured with `CallerArgumentExpression`), then shows expected and actual values. Strings longer than 200
characters and collections longer than 10 items are truncated in messages. Every method except `Fail` accepts an
optional `message` that is appended to the failure text.

| Method | Passes when | Returns |
|---|---|---|
| `True(condition)` | `condition` is true | |
| `False(condition)` | `condition` is false | |
| `Equal<T>(expected, actual)` | `EqualityComparer<T>.Default.Equals(expected, actual)` | |
| `NotEqual<T>(unexpected, actual)` | the values are not equal | |
| `Null(value)` | `value` is null | |
| `NotNull<T>(value)` | `value` is not null (reference or nullable value type) | the value, non-null |
| `Empty<T>(collection)` | the collection has no items | |
| `NotEmpty<T>(collection)` | the collection has at least one item | |
| `Count<T>(expected, collection)` | the collection has exactly `expected` items | |
| `Single<T>(collection)` | the collection has exactly one item | the item |
| `Single<T>(collection, predicate)` | exactly one item matches | the matching item |
| `Contains<T>(expected, collection)` | the collection contains `expected` | |
| `SequenceEqual<T>(expected, actual)` | the sequences are equal item by item; a failure reports the first differing index | |
| `Throws<TException>(action)` | `action` throws `TException` or a derived type | the exception |
| `ThrowsAsync<TException>(Func<Task>)` | the task faults with `TException` or a derived type | the exception |
| `Fail(message)` | never | |

When `Throws` or `ThrowsAsync` observes a different exception, that exception becomes the `InnerException` of the
assertion failure.
