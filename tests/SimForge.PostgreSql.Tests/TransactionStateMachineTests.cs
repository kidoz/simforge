using System.Text;
using Xunit;

namespace SimForge.PostgreSql.Tests;

/// <summary>
/// Seeded random walks over transactional operations, compared step by step with an independent reference model. The
/// invariants are that committed state changes only through successful commits or autocommit writes, that rollback and
/// failed commits expose no partial writes, and that constraints hold at every step.
/// </summary>
public sealed class TransactionStateMachineTests
{
    private const int Steps = 300;
    private static readonly string?[] Emails = [null, "a", "b", "c"];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 40)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Simulation_matches_the_reference_model(int seed)
    {
        var random = new Random(seed);
        var history = new StringBuilder();
        await using var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { ScenarioId = "state-machine", Seed = seed });
        var database = environment.AddPostgreSqlDatabase("db", schema => schema.Table("accounts", table => table
            .Column("id", ColumnType.Integer, primaryKey: true)
            .Column("email", ColumnType.Text, unique: true)
            .Column("balance", ColumnType.BigInt, notNull: true)));
        await environment.InitializeAsync(TestContext.Current.CancellationToken);

        var model = new Model();
        PostgreSqlTransaction? transaction = null;

        for (var step = 0; step < Steps; step++)
        {
            var action = random.Next(8);
            var id = random.Next(1, 6);
            var email = Emails[random.Next(Emails.Length)];
            var balance = random.NextInt64(-5, 6);
            var useTransaction = transaction is not null && random.Next(4) != 0;
            history.Append(step).Append(": ");

            try
            {
                switch (action)
                {
                    case 0:
                        history.Append("begin");
                        var expectBegin = model.Transaction is null ? Expect.Success : Expect.Unsupported;
                        Check(expectBegin, () => transaction = database.BeginTransaction());
                        if (expectBegin == Expect.Success)
                        {
                            model.Begin();
                        }

                        break;
                    case 1 or 2:
                        history.Append($"insert id={id} email={email ?? "null"} tx={useTransaction}");
                        Write(useTransaction, model.PredictInsert(useTransaction, id, email), () =>
                        {
                            var values = new Dictionary<string, object?> { ["id"] = id, ["email"] = email, ["balance"] = balance };
                            if (useTransaction)
                            {
                                transaction!.Insert("accounts", values);
                            }
                            else
                            {
                                database.Insert("accounts", values);
                            }
                        }, view => view[id] = (email, balance));
                        break;
                    case 3:
                        history.Append($"update id={id} email={email ?? "null"} tx={useTransaction}");
                        var updateExpectation = model.PredictUpdate(useTransaction, id, email);
                        var updated = false;
                        Write(useTransaction, updateExpectation, () =>
                        {
                            var changes = new Dictionary<string, object?> { ["email"] = email, ["balance"] = balance };
                            updated = useTransaction ? transaction!.Update("accounts", id, changes) : database.Update("accounts", id, changes);
                        }, view =>
                        {
                            if (view.ContainsKey(id))
                            {
                                view[id] = (email, balance);
                            }
                        });
                        if (updateExpectation == Expect.Success)
                        {
                            Assert.Equal(model.View(useTransaction).ContainsKey(id), updated);
                        }

                        break;
                    case 4:
                        history.Append($"delete id={id} tx={useTransaction}");
                        Write(useTransaction, model.PredictDelete(useTransaction), () =>
                        {
                            _ = useTransaction ? transaction!.Delete("accounts", id) : database.Delete("accounts", id);
                        }, view => view.Remove(id));
                        break;
                    case 5:
                        history.Append("commit");
                        if (transaction is null)
                        {
                            break;
                        }

                        Check(model.Transaction!.Failed ? Expect.InFailedTransaction : Expect.Success, transaction.Commit);
                        model.Commit();
                        transaction = null;
                        break;
                    case 6:
                        history.Append("rollback");
                        transaction?.Rollback();
                        model.Rollback();
                        transaction = null;
                        break;
                    default:
                        history.Append($"get id={id}");
                        var row = database.Get("accounts", id);
                        Assert.Equal(model.Committed.ContainsKey(id), row is not null);
                        break;
                }

                history.AppendLine();
                AssertMatches(database.Scan("accounts"), model.Committed);
                if (transaction is not null && model.Transaction is { Failed: false } active)
                {
                    AssertMatches(transaction.Scan("accounts"), active.View);
                }
            }
            catch (Exception exception) when (exception is not ReproductionException)
            {
                throw new ReproductionException($"Seed {seed} diverged at step {step}. History:{Environment.NewLine}{history}", exception);
            }
        }

        void Write(bool inTransaction, Expect expectation, Action action, Action<Dictionary<int, (string? Email, long Balance)>> apply)
        {
            Check(expectation, action);
            if (expectation == Expect.Success)
            {
                apply(model.View(inTransaction));
            }
            else if (expectation is Expect.UniqueViolation && inTransaction)
            {
                model.Transaction!.Failed = true;
            }
        }
    }

    private static void Check(Expect expectation, Action action)
    {
        switch (expectation)
        {
            case Expect.Success:
                action();
                break;
            case Expect.Unsupported:
                Assert.Throws<UnsupportedCapabilityException>(action);
                break;
            case Expect.UniqueViolation:
                Assert.Equal(PostgreSqlErrorCodes.UniqueViolation, Assert.Throws<PostgreSqlStorageException>(action).ErrorCode);
                break;
            case Expect.InFailedTransaction:
                Assert.Equal(PostgreSqlErrorCodes.InFailedTransaction, Assert.Throws<PostgreSqlStorageException>(action).ErrorCode);
                break;
        }
    }

    private static void AssertMatches(IReadOnlyList<Row> rows, Dictionary<int, (string? Email, long Balance)> expected)
    {
        var actual = rows.Select(row => (Id: row.Get<int>("id"), Email: (string?)row["email"], Balance: row.Get<long>("balance"))).ToList();
        Assert.Equal(expected.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value.Email, pair.Value.Balance)), actual);
        var emails = actual.Where(row => row.Email is not null).Select(row => row.Email).ToList();
        Assert.Equal(emails.Count, emails.Distinct().Count());
    }

    private enum Expect
    {
        Success,
        Unsupported,
        UniqueViolation,
        InFailedTransaction,
    }

    private sealed class Model
    {
        public Dictionary<int, (string? Email, long Balance)> Committed { get; } = [];

        public TransactionModel? Transaction { get; private set; }

        public Dictionary<int, (string? Email, long Balance)> View(bool inTransaction) => inTransaction ? Transaction!.View : Committed;

        public void Begin() => Transaction = new TransactionModel(new Dictionary<int, (string? Email, long Balance)>(Committed));

        public void Commit()
        {
            if (Transaction is { Failed: false } transaction)
            {
                Committed.Clear();
                foreach (var (key, value) in transaction.View)
                {
                    Committed[key] = value;
                }
            }

            Transaction = null;
        }

        public void Rollback() => Transaction = null;

        public Expect PredictInsert(bool inTransaction, int id, string? email) =>
            PredictWrite(inTransaction) ?? (View(inTransaction).ContainsKey(id) || EmailTaken(View(inTransaction), email, exceptId: null)
                ? Expect.UniqueViolation
                : Expect.Success);

        public Expect PredictUpdate(bool inTransaction, int id, string? email) =>
            PredictWrite(inTransaction) ?? (View(inTransaction).ContainsKey(id) && EmailTaken(View(inTransaction), email, exceptId: id)
                ? Expect.UniqueViolation
                : Expect.Success);

        public Expect PredictDelete(bool inTransaction) => PredictWrite(inTransaction) ?? Expect.Success;

        private Expect? PredictWrite(bool inTransaction)
        {
            if (inTransaction)
            {
                return Transaction!.Failed ? Expect.InFailedTransaction : null;
            }

            return Transaction is not null ? Expect.Unsupported : null;
        }

        private static bool EmailTaken(Dictionary<int, (string? Email, long Balance)> view, string? email, int? exceptId) =>
            email is not null && view.Any(pair => pair.Key != exceptId && pair.Value.Email == email);
    }

    private sealed class TransactionModel(Dictionary<int, (string? Email, long Balance)> view)
    {
        public Dictionary<int, (string? Email, long Balance)> View { get; } = view;

        public bool Failed { get; set; }
    }

    private sealed class ReproductionException(string message, Exception innerException) : Exception(message, innerException);
}
