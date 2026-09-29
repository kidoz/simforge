# About the PostgreSQL model

`SimForge.PostgreSql` models the part of PostgreSQL an application's persistence layer relies on: tables, keys,
constraints, and transactions. It does not model the SQL language. This page explains that choice and the
restrictions that come with it.

## An application contract, not a database engine

A repository layer rarely needs arbitrary SQL. It needs "store this row", "find by key", "these changes commit together
or not at all", and "this value must be unique". SimForge offers exactly those operations as an explicit API, and tests
use them to implement the application's repository interfaces.

Emulating SQL would be a different project. Parsing, planning, joins, and type coercion each carry a long tail of
behavior that must match PostgreSQL to be trustworthy, and a partial emulation invites false confidence. Several future
directions remain open, each as a separately named mode with its own verification:
- a deliberately limited SQL surface
- an embedded PostgreSQL-derived engine
- a loopback protocol frontend

None of them is implied today.

## One writer at a time

Real PostgreSQL runs concurrent transactions under MVCC with a chosen isolation level. Modeling that faithfully means
modeling snapshots, locks, and serialization failures, and verifying all of it against the real server. Instead, the
SimForge model allows one active transaction per database and rejects overlapping writers with an explicit
`UnsupportedCapabilityException`.

That is an honest restriction rather than an approximation. Within it, the guarantees that matter to most workflows
hold exactly:
- a transaction reads its own writes
- nobody else sees uncommitted writes
- a commit publishes every change at once
- a rollback leaves no trace

The model is deliberately not called Read Committed, Repeatable Read, or Serializable, because it is none of those.

Internally, each table is an immutable value. A transaction builds candidate versions of the tables it touches. Commit
validates the touched rows and swaps all candidates in with a single assignment. Partial visibility is therefore
impossible by construction.

## Errors behave like PostgreSQL's, with one deliberate difference

In PostgreSQL, any error inside a transaction block aborts the transaction, and later statements fail until the block
ends. SimForge does the same for constraint violations, unknown tables and columns, type mismatches, and injected
faults.

The difference concerns `COMMIT` on an aborted transaction. PostgreSQL answers with `ROLLBACK` and raises no error. A
caller that checks only for exceptions can therefore believe it committed. SimForge throws `in_failed_sql_transaction`
instead, so a test cannot mistake a rolled-back transaction for a successful one.

Requests outside the model, such as changing a primary key, are rejected before anything changes, and they do *not*
abort the transaction. They are limitations of SimForge, not errors the database would report.

## Strict values

Each column type maps to exactly one CLR type, and values are not converted: an `int` is not accepted for a `bigint`
column. Silent widening would hide mistakes that a real driver might surface differently. Equality and ordering rules
are defined per type and published, including where they differ from PostgreSQL, such as ordinal text ordering instead
of collations.

Constraint-violation messages leave out the conflicting value, and the journal records payloads only when asked to.
Test data often resembles real data, and it should not leak into logs by default.

## Related

- [How to back a repository with simulated storage](../how-to/back-a-repository-with-simulated-storage.md)
- [SimForge.PostgreSql](../reference/postgresql.md) (reference)
- [About faults and ambiguous outcomes](about-faults-and-ambiguous-outcomes.md)
