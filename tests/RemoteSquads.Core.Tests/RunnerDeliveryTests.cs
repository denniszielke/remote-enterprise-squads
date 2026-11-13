using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteSquads.Core;

static class RunnerDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly RunnerSession Session = new("runner-project", "runner-run", "session-fixture", 7);
    private static readonly IdempotencyScope Scope = new("operator-fixture", "client-fixture",
        Session.ProjectId, "message", Session.RunId);

    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Runner composes admission journal fake delivery status and restart replay", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            f.Adapter.Handler = (delivery, _) =>
            {
                Check(delivery.Target.Kind == RecipientKind.Member && delivery.Target.MemberId == "rusty-fixture");
                Check(delivery.Target.DeliveryMode == DeliveryMode.Enqueue && delivery.Target.TeamVersion == "pinned-fixture");
                Check(delivery.PayloadReference == "protected-fixture" && delivery.PayloadDigest == Digest);
                Check(f.Status(id) == CommandStatus.Accepted && f.Events().Length == 1);
                return ValueTask.FromResult(Ack(delivery));
            };
            Check(f.Deliver(id).Status == CommandStatus.Applied);
            Check(f.NewRunner().DeliverAsync(Session, id).AsTask().GetAwaiter().GetResult().Status == CommandStatus.Applied);
            var replay = f.Admission.SubmitAsync(Submission("key-1")).AsTask().GetAwaiter().GetResult();
            Check(replay.Replayed && replay.CommandId == id && replay.Status == CommandStatus.Applied);
            Check(f.Adapter.Calls == 1 && f.Events().Select(Kind).SequenceEqual(["started", "observed", "settled"]));
        }),
        ("Unavailable SDK fails explicitly before any journal claim or success status", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            var runner = new RunnerDeliveryService(f.Store, f.Authority, new UnavailableRunnerSessionAdapter(),
                f.Statuses, TimeSpan.FromSeconds(2));
            Error("sdk_unavailable", () => runner.DeliverAsync(Session, id).AsTask().GetAwaiter().GetResult());
            Check(f.Store.Journal is null && f.Status(id) == CommandStatus.Accepted && f.Adapter.Calls == 0);
        }),
        ("Runner requires committed admission and authenticates before reading storage", () =>
        {
            var f = new Fixture();
            f.Authority.Allowed = false;
            Error("forbidden", () => f.Deliver("command-" + new string('a', 64)));
            Check(f.Store.Reads == 0 && f.Adapter.Calls == 0);
            f.Authority.Allowed = true;
            Error("resource_not_found", () => f.Deliver("command-" + new string('a', 64)));
            Check(f.Store.Journal is null);
            var id = f.Admit();
            f.Authority.Generation = 8;
            int reads = f.Store.Reads;
            Error("lease_fenced", () => f.Deliver(id));
            Check(f.Store.Reads == reads && f.Adapter.Calls == 0);
        }),
        ("Pre-dispatch cancellation never journals or invokes the adapter", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Throws<OperationCanceledException>(() => f.Runner.DeliverAsync(Session, id, cancel.Token).AsTask().GetAwaiter().GetResult());
            Check(f.Store.Journal is null && f.Adapter.Calls == 0 && f.Status(id) == CommandStatus.Accepted);
        }),
        ("Lost acknowledgement remains uncertain across recovery and blocks later commands", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            f.Adapter.Handler = (_, _) => throw new IOException("synthetic acknowledgement loss");
            Check(f.Deliver(id).Status == CommandStatus.Uncertain);
            Check(f.NewRunner().RecoverAsync(Session).AsTask().GetAwaiter().GetResult()!.Status == CommandStatus.Uncertain);
            Check(f.Deliver(id).Status == CommandStatus.Uncertain);
            Error("runner_busy", () => f.Deliver(f.Admit("key-2")));
            Check(f.Adapter.Calls == 1 && f.Events().Length == 2);
        }),
        ("Acknowledgements bind exact session command attempt sequence and known outcome", () =>
        {
            foreach (var corrupt in new Func<RunnerAcknowledgement, RunnerAcknowledgement>[]
            {
                ack => ack with { Session = new(Session.ProjectId, Session.RunId, "other-session", 7) },
                ack => ack with { Session = new(Session.ProjectId, Session.RunId, Session.SessionId, 8) },
                ack => ack with { CommandId = "command-" + new string('f', 64) },
                ack => ack with { Sequence = 99 },
                ack => ack with { AttemptId = new string('0', 32) },
                ack => ack with { Outcome = CommandStatus.Accepted },
                ack => ack with { Outcome = (CommandStatus)99 }
            })
            {
                var f = new Fixture();
                var id = f.Admit();
                f.Adapter.Handler = (delivery, _) => ValueTask.FromResult(corrupt(Ack(delivery)));
                Check(f.Deliver(id).Status == CommandStatus.Uncertain && f.Adapter.Calls == 1);
            }
        }),
        ("Journal failures including committed lost responses never authorize SDK dispatch", () =>
        {
            foreach (bool committed in new[] { false, true })
            {
                var f = new Fixture();
                var id = f.Admit();
                f.Store.StartFailure = committed ? "after" : "before";
                Throws<IOException>(() => f.Deliver(id));
                Check(f.Adapter.Calls == 0 && f.Status(id) == CommandStatus.Accepted);
                if (committed)
                {
                    Error("runner_busy", () => f.Deliver(id));
                    Check(f.NewRunner().RecoverAsync(Session).AsTask().GetAwaiter().GetResult()!.Status == CommandStatus.Uncertain);
                }
                else Check(f.Store.Journal is null);
            }
        }),
        ("Crash after delivery before observation recovers conservatively without redispatch", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            f.Store.FailObservation = true;
            Throws<IOException>(() => f.Deliver(id));
            Check(f.Adapter.Calls == 1 && f.Events().Length == 1 && f.Status(id) == CommandStatus.Accepted);
            var recovered = f.NewRunner().RecoverAsync(Session).AsTask().GetAwaiter().GetResult();
            Check(recovered!.Status == CommandStatus.Uncertain && f.Adapter.Calls == 1);
        }),
        ("Observed acknowledgement survives status failure and ambiguous committed status response", () =>
        {
            foreach (bool committed in new[] { false, true })
            {
                var f = new Fixture();
                var id = f.Admit();
                f.Store.StatusFailure = committed ? "after" : "before";
                Throws<IOException>(() => f.Deliver(id));
                Check(f.Events().Length == 2 && f.Adapter.Calls == 1);
                Check(f.Status(id) == (committed ? CommandStatus.Applied : CommandStatus.Accepted));
                Check(f.NewRunner().RecoverAsync(Session).AsTask().GetAwaiter().GetResult()!.Status == CommandStatus.Applied);
                Check(f.Events().Length == 3 && f.Adapter.Calls == 1 && f.Status(id) == CommandStatus.Applied);
            }
        }),
        ("Settlement write failure retries bookkeeping only and preserves observed failure", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            f.Adapter.Handler = (delivery, _) => ValueTask.FromResult(Ack(delivery) with { Outcome = CommandStatus.Failed });
            f.Store.FailSettlement = true;
            Throws<IOException>(() => f.Deliver(id));
            Check(f.Status(id) == CommandStatus.Failed && f.Events().Length == 2);
            Check(f.NewRunner().DeliverAsync(Session, id).AsTask().GetAwaiter().GetResult().Status == CommandStatus.Failed);
            Check(f.Events().Length == 3 && f.Adapter.Calls == 1);
            Check(f.Deliver(f.Admit("key-2")).Status == CommandStatus.Failed && f.Adapter.Calls == 2);
        }),
        ("One active delivery excludes duplicates and later commands while cancellation can settle", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            var next = f.Admit("key-2");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Adapter.Handler = async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertionFailure("Cancellation should interrupt the synthetic wait.");
            };
            using var cancel = new CancellationTokenSource();
            var active = f.Runner.DeliverAsync(Session, id, cancel.Token).AsTask();
            try
            {
                entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Error("runner_busy", () => f.NewRunner().DeliverAsync(Session, id).AsTask().GetAwaiter().GetResult());
                Error("runner_busy", () => f.NewRunner().DeliverAsync(Session, next).AsTask().GetAwaiter().GetResult());
            }
            finally { cancel.Cancel(); }
            Check(active.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult().Status == CommandStatus.Uncertain);
            Check(f.Status(id) == CommandStatus.Uncertain && f.Adapter.Calls == 1);
        }),
        ("Caller cancellation after a valid acknowledgement cannot downgrade observed delivery", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            using var cancel = new CancellationTokenSource();
            f.Adapter.Handler = (delivery, _) =>
            {
                cancel.Cancel();
                return ValueTask.FromResult(Ack(delivery));
            };
            Check(f.Runner.DeliverAsync(Session, id, cancel.Token).AsTask().GetAwaiter().GetResult().Status == CommandStatus.Applied);
            Check(f.Events().Length == 3 && f.Status(id) == CommandStatus.Applied);
        }),
        ("Recovery requires explicit process containment authority before journal reads", () =>
        {
            var f = new Fixture();
            f.Authority.Contained = false;
            Error("reconciliation_required", () => f.Runner.RecoverAsync(Session).AsTask().GetAwaiter().GetResult());
            Check(f.Store.Reads == 0 && f.Adapter.Calls == 0);
            f.Authority.Contained = true;
            Check(f.Runner.RecoverAsync(Session).AsTask().GetAwaiter().GetResult() is null);
        }),
        ("A different session or generation cannot reset or take over a retained journal", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            f.Deliver(id);
            f.Authority.ExpectedSession = new(Session.ProjectId, Session.RunId, "replacement-session", 7);
            Error("reconciliation_required", () => f.Runner.DeliverAsync(f.Authority.ExpectedSession, id).AsTask().GetAwaiter().GetResult());
            f.Authority.ExpectedSession = new(Session.ProjectId, Session.RunId, Session.SessionId, 8);
            f.Authority.Generation = 8;
            Error("lease_fenced", () => f.Runner.DeliverAsync(f.Authority.ExpectedSession, id).AsTask().GetAwaiter().GetResult());
            Check(f.Adapter.Calls == 1 && f.Events().Length == 3);
        }),
        ("Corrupt journal transitions and bindings fail closed before adapter invocation", () =>
        {
            foreach (var corrupt in new Action<JsonObject>[]
            {
                journal => journal["ttl"] = 60,
                journal => journal["schema_version"] = "2.0",
                journal => journal["events"] = new JsonArray(),
                journal => journal["session"]!["session_id"] = "other-session",
                journal => journal["events"]![0]!["kind"] = "observed",
                journal => journal["events"]![0]!["sequence"] = 2,
                journal => journal["events"]![1]!["attempt_id"] = new string('0', 32),
                journal => journal["events"]![2]!["outcome"] = "uncertain",
                journal => journal["events"]![0]!["fingerprint"] = "sha256:" + new string('b', 64)
            })
            {
                var f = new Fixture();
                var id = f.Admit();
                f.Deliver(id);
                f.Store.MutateJournal(corrupt);
                Error("reconciliation_required", () => f.Deliver(f.Admit("key-2")));
                Check(f.Adapter.Calls == 1);
            }
        }),
        ("Bounded journal reserves completion capacity and refuses a thirty-third dispatch", () =>
        {
            var f = new Fixture();
            var ids = Enumerable.Range(1, 32).Select(i => f.Admit($"bounded-{i}")).ToArray();
            foreach (var id in ids) Check(f.Deliver(id).Status == CommandStatus.Applied);
            Error("journal_capacity", () => f.Deliver(f.Admit("overflow")));
            Check(f.Adapter.Calls == 32 && f.Events().Length == 96);
            Check(f.Deliver(ids[0]).Status == CommandStatus.Applied && f.Adapter.Calls == 32);
        }),
        ("Fresh lease loss after journal claim fences dispatch and retains blocking evidence", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            f.Store.AfterStart = () => f.Authority.Generation = 8;
            Error("lease_fenced", () => f.Deliver(id));
            Check(f.Adapter.Calls == 0 && f.Events().Length == 1);
            Error("lease_fenced", () => f.Runner.RecoverAsync(Session).AsTask().GetAwaiter().GetResult());
            f.Authority.Generation = 7; // Synthetic authority restored solely to exercise contained recovery.
            Check(f.NewRunner().RecoverAsync(Session).AsTask().GetAwaiter().GetResult()!.Status == CommandStatus.Uncertain);
            Check(f.Adapter.Calls == 0);
        }),
        ("Recovery CAS prevents a late live-owner acknowledgement from replacing uncertainty", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Adapter.Handler = async (delivery, _) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return Ack(delivery);
            };
            var active = f.Runner.DeliverAsync(Session, id).AsTask();
            entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            try
            {
                Check(f.NewRunner().RecoverAsync(Session).AsTask().GetAwaiter().GetResult()!.Status == CommandStatus.Uncertain);
            }
            finally { release.SetResult(); }
            Error("version_conflict", () => active.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Check(f.Status(id) == CommandStatus.Uncertain && f.Adapter.Calls == 1 && f.Events().Length == 2);
        }),
        ("Concurrent initial claims authorize at most one adapter invocation", () =>
        {
            var f = new Fixture();
            var id = f.Admit();
            var results = Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
            {
                try { await f.NewRunner().DeliverAsync(Session, id); }
                catch (DomainException error) when (error.Code is "version_conflict" or "runner_busy") { }
            }))).WaitAsync(TimeSpan.FromSeconds(10));
            results.GetAwaiter().GetResult();
            // A bookkeeping CAS loser is recoverable; no dispatch is replayed.
            f.NewRunner().RecoverAsync(Session).AsTask().GetAwaiter().GetResult();
            Check(f.Adapter.Calls == 1 && f.Status(id) == CommandStatus.Applied && f.Events().Length == 3);
        })
    ];

    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static MessageSubmission Submission(string key) => new(Scope, key,
        new(new("pinned-fixture", ["rusty-fixture"]), RecipientKind.Member, DeliveryMode.Enqueue, "rusty-fixture"),
        "protected-fixture", Digest, Session.Generation);
    private static RunnerAcknowledgement Ack(RunnerDelivery delivery)
        => new(delivery.Session, delivery.CommandId, delivery.Sequence, delivery.AttemptId, CommandStatus.Applied);
    private static string? Kind(JsonElement entry) => entry.GetProperty("kind").GetString();
    private static void Check(bool condition)
    {
        if (!condition) throw new AssertionFailure("Runner integration assertion did not hold.");
    }
    private static void Error(string code, Action action)
    {
        try { action(); }
        catch (DomainException error) { Check(error.Code == code); return; }
        throw new AssertionFailure($"Expected runner error {code}.");
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new AssertionFailure($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture
    {
        public readonly Store Store = new();
        public readonly Authority Authority;
        public readonly Adapter Adapter = new();
        public readonly MessageAdmissionService Admission;
        public readonly CommandStatusService Statuses;
        public readonly RunnerDeliveryService Runner;
        public Fixture()
        {
            Authority = new(Store);
            Admission = new(Store, Authority, new Clock(), TimeSpan.FromHours(1));
            Statuses = new(Store, Authority);
            Runner = NewRunner();
        }
        public RunnerDeliveryService NewRunner() => new(Store, Authority, Adapter, Statuses, TimeSpan.FromSeconds(2));
        public string Admit(string key = "key-1") => Admission.SubmitAsync(Submission(key)).AsTask().GetAwaiter().GetResult().CommandId;
        public CommandStatusReceipt Deliver(string id) => Runner.DeliverAsync(Session, id).AsTask().GetAwaiter().GetResult();
        public CommandStatus Status(string id) => Statuses.GetAsync(Session.ProjectId, Session.RunId, id).AsTask().GetAwaiter().GetResult().Status;
        public JsonElement[] Events() => Store.Journal!.Value.GetProperty("events").EnumerateArray().ToArray();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Adapter : IRunnerSessionAdapter
    {
        public int Calls;
        public Func<RunnerDelivery, CancellationToken, ValueTask<RunnerAcknowledgement>> Handler =
            (delivery, _) => ValueTask.FromResult(Ack(delivery));
        public void RequireAvailable() { }
        public ValueTask<RunnerAcknowledgement> DeliverAsync(RunnerDelivery delivery, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Handler(delivery, cancellationToken);
        }
    }

    // Synthetic identity/lease/policy/evidence resolver, not production authentication or process fencing.
    private sealed class Authority(Store store) : IMessageAdmissionAuthorizer, ICommandStatusAuthorizer, IRunnerDeliveryAuthority
    {
        public bool Allowed = true;
        public bool Contained = true;
        public long Generation = 7;
        public RunnerSession ExpectedSession = Session;
        public ValueTask AuthorizeSessionAsync(RunnerSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Allowed) throw new DomainException("forbidden", "Synthetic runner denied.");
            if (session != ExpectedSession || session.Generation != Generation)
                throw new DomainException("lease_fenced", "Synthetic writer fenced.");
            return ValueTask.CompletedTask;
        }
        public async ValueTask AuthorizeDeliveryAsync(RunnerDelivery delivery, CancellationToken cancellationToken)
        {
            await AuthorizeSessionAsync(delivery.Session, cancellationToken);
            Check(delivery.Target.TeamVersion == "pinned-fixture" && delivery.Target.MemberId == "rusty-fixture");
        }
        public async ValueTask AuthorizeRecoveryAsync(RunnerSession session, CancellationToken cancellationToken)
        {
            await AuthorizeSessionAsync(session, cancellationToken);
            if (!Contained) throw new DomainException("reconciliation_required", "Synthetic process not contained.");
        }
        public ValueTask AuthorizeAsync(IdempotencyScope scope, MessageTarget target, long leaseGeneration,
            CancellationToken cancellationToken)
        {
            Check(scope == Scope && target.TeamVersion == "pinned-fixture" && leaseGeneration == Generation);
            return ValueTask.CompletedTask;
        }
        public ValueTask AuthorizeReadAsync(string projectId, string runId, CancellationToken cancellationToken)
        {
            Check(projectId == Session.ProjectId && runId == Session.RunId);
            return ValueTask.CompletedTask;
        }
        public async ValueTask AuthorizeOutcomeAsync(string projectId, string runId, string commandId, long generation,
            CommandStatus outcome, string evidenceReference, string? reconciliationReference, CancellationToken cancellationToken)
        {
            await AuthorizeSessionAsync(ExpectedSession, cancellationToken);
            Check(projectId == ExpectedSession.ProjectId && runId == ExpectedSession.RunId &&
                generation == ExpectedSession.Generation && reconciliationReference is null);
            var journal = store.Journal!.Value;
            Check(journal.GetProperty("session").GetProperty("session_id").GetString() == ExpectedSession.SessionId);
            Check(journal.GetProperty("session").GetProperty("generation").GetInt64() == generation);
            var prefix = journal.GetProperty("id").GetString() + "/events/";
            Check(evidenceReference.StartsWith(prefix, StringComparison.Ordinal));
            int index = int.Parse(evidenceReference[prefix.Length..]);
            var evidence = journal.GetProperty("events")[index - 1];
            var started = journal.GetProperty("events")[index - 2];
            Check(Kind(evidence) == "observed" && Kind(started) == "started");
            Check(evidence.GetProperty("command_id").GetString() == commandId &&
                evidence.GetProperty("sequence").GetInt64() == started.GetProperty("sequence").GetInt64() &&
                evidence.GetProperty("attempt_id").GetString() == started.GetProperty("attempt_id").GetString());
            Check(evidence.GetProperty("outcome").Deserialize<CommandStatus>(Protocol.CreateJsonOptions()) == outcome);
        }
    }

    private sealed class Store : IControlDocumentStore
    {
        private readonly object gate = new();
        private readonly Dictionary<(string Partition, string Id), VersionedDocument> documents = [];
        private int version;
        public int Reads;
        public string? StartFailure;
        public string? StatusFailure;
        public bool FailObservation;
        public bool FailSettlement;
        public Action? AfterStart;
        public JsonElement? Journal
        {
            get { lock (gate) return documents.Values.Select(d => d.Body).FirstOrDefault(d => d.TryGetProperty("events", out _)) is var body && body.ValueKind != JsonValueKind.Undefined ? body : null; }
        }
        public ValueTask<VersionedDocument?> ReadAsync(string partitionKey, string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                Reads++;
                return ValueTask.FromResult(documents.GetValueOrDefault((partitionKey, id)));
            }
        }
        public ValueTask<bool> CreateAsync(string partitionKey, string id, JsonElement document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (documents.ContainsKey((partitionKey, id))) return ValueTask.FromResult(false);
                bool start = id.StartsWith("runner-journal-", StringComparison.Ordinal);
                string? failure = start ? StartFailure : null;
                if (start) StartFailure = null;
                if (failure == "before") throw new IOException("Synthetic journal unavailable.");
                documents[(partitionKey, id)] = new(document.Clone(), (++version).ToString());
                if (start) AfterStart?.Invoke();
                if (failure == "after") throw new IOException("Synthetic committed journal response lost.");
                return ValueTask.FromResult(true);
            }
        }
        public ValueTask<bool> ReplaceAsync(string partitionKey, string id, JsonElement document, string expectedETag,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!documents.TryGetValue((partitionKey, id), out var prior) || prior.ETag != expectedETag)
                    return ValueTask.FromResult(false);
                bool journal = id.StartsWith("runner-journal-", StringComparison.Ordinal);
                if (journal)
                {
                    var entries = document.GetProperty("events");
                    string? kind = Kind(entries[entries.GetArrayLength() - 1]);
                    if (kind == "observed" && FailObservation)
                    { FailObservation = false; throw new IOException("Synthetic observation unavailable."); }
                    if (kind == "settled" && FailSettlement)
                    { FailSettlement = false; throw new IOException("Synthetic settlement unavailable."); }
                }
                string? failure = journal ? null : StatusFailure;
                if (!journal) StatusFailure = null;
                if (failure == "before") throw new IOException("Synthetic status unavailable.");
                documents[(partitionKey, id)] = new(document.Clone(), (++version).ToString());
                if (failure == "after") throw new IOException("Synthetic committed status response lost.");
                return ValueTask.FromResult(true);
            }
        }
        public void MutateJournal(Action<JsonObject> mutation)
        {
            lock (gate)
            {
                var key = documents.Keys.Single(k => k.Id.StartsWith("runner-journal-", StringComparison.Ordinal));
                var body = JsonNode.Parse(documents[key].Body.GetRawText())!.AsObject();
                mutation(body);
                documents[key] = new(JsonSerializer.SerializeToElement(body), (++version).ToString());
            }
        }
    }
}
