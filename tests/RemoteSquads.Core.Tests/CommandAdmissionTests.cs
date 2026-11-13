using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteSquads.Core;

static class CommandAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly IdempotencyScope Scope = new("subject-fixture", "client-fixture", "project-fixture", "message", "run-fixture");
    private static readonly string PayloadDigest = "sha256:" + new string('a', 64);

    public static IEnumerable<(string Name, Action Run)> Cases =>
    [
        ("Admission atomically records binding and intent before returning accepted", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope);
            var service = Service(store, authorizer);
            var receipt = service.SubmitAsync(Submission()).GetAwaiter().GetResult();
            Check(!receipt.Replayed && receipt.Status == CommandStatus.Accepted);
            var document = store.Documents.Values.Single();
            Check(document.GetProperty("id").GetString() == receipt.CommandId);
            Check(document.GetProperty("scope").GetProperty("target_id").GetString() == Scope.TargetId);
            Check(document.GetProperty("payload_reference").GetString() == "protected-payload-1");
            Check(document.GetProperty("fingerprint").GetProperty("payload_digest").GetString() == Submission().Fingerprint.PayloadDigest);
            Check(document.GetProperty("ttl").GetInt32() == -1);
            Check(store.Reads == 0 && authorizer.Calls == 1);
        }),
        ("Admission composes with the real Cosmos adapter across conflict and replay", () =>
        {
            using var transport = new CommandTransport();
            using var store = new CosmosControlDocumentStore(new("https://fixture.documents.azure.com"), "fixture-db",
                "fixture-container", _ => ValueTask.FromResult("synthetic-fixture-token"), transport);
            var service = new MessageAdmissionService(store, new FixtureAuthorizer(Scope), new FixtureClock(Now),
                TimeSpan.FromHours(1));
            var first = service.SubmitAsync(Submission()).GetAwaiter().GetResult();
            var recreated = new MessageAdmissionService(store, new FixtureAuthorizer(Scope), new FixtureClock(Now),
                TimeSpan.FromHours(1));
            var replay = recreated.SubmitAsync(Submission()).GetAwaiter().GetResult();
            Check(!first.Replayed && replay.Replayed && first.CommandId == replay.CommandId);
            Check(replay.Status == CommandStatus.Accepted && transport.Creates == 2 && transport.Reads == 1);
        }),
        ("Admission retries survive service restart and replay the recorded command status", () =>
        {
            var store = new FixtureStore();
            var first = Service(store, new(Scope)).SubmitAsync(Submission()).GetAwaiter().GetResult();
            store.Mutate(document =>
            {
                document["status"] = "uncertain";
                document["outcome_reference"] = "fixture-journal";
            });
            var authorizer = new FixtureAuthorizer(Scope);
            var restarted = Service(store, authorizer);
            var replay = restarted.SubmitAsync(Submission(payloadReference: "another-protected-reference"))
                .GetAwaiter().GetResult();
            Check(replay.Replayed && replay.CommandId == first.CommandId && replay.Status == CommandStatus.Uncertain);
            Check(store.Documents.Count == 1 && authorizer.Calls == 1);
            Check(store.Documents.Values.Single().GetProperty("payload_reference").GetString() == "protected-payload-1");
        }),
        ("Admission rejects semantic payload recipient and generation mismatches", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope);
            var service = Service(store, authorizer);
            service.SubmitAsync(Submission()).GetAwaiter().GetResult();
            Error("idempotency_mismatch", () => service.SubmitAsync(
                Submission(payloadDigest: "sha256:" + new string('b', 64))).GetAwaiter().GetResult());
            Error("idempotency_mismatch", () => service.SubmitAsync(
                Submission(memberId: "member-fixture")).GetAwaiter().GetResult());
            Error("idempotency_mismatch", () => service.SubmitAsync(
                Submission(mode: DeliveryMode.Enqueue)).GetAwaiter().GetResult());
            Error("invalid_request", () => Submission(
                new(Scope.SubjectId, Scope.ClientId, Scope.ProjectId, "steer", Scope.TargetId)));
            authorizer.Generation = 8;
            Error("idempotency_mismatch", () => service.SubmitAsync(
                Submission(generation: 8)).GetAwaiter().GetResult());
            Check(store.Documents.Count == 1);
        }),
        ("Admission scopes message keys by subject client project and target", () =>
        {
            var store = new FixtureStore();
            var scopes = new[] { Scope,
                new IdempotencyScope("another-subject", Scope.ClientId, Scope.ProjectId, Scope.Operation, Scope.TargetId),
                new(Scope.SubjectId, "another-client", Scope.ProjectId, Scope.Operation, Scope.TargetId),
                new(Scope.SubjectId, Scope.ClientId, "another-project", Scope.Operation, Scope.TargetId),
                new(Scope.SubjectId, Scope.ClientId, Scope.ProjectId, Scope.Operation, "another-run") };
            var ids = scopes.Select(scope => Service(store, new(scope)).SubmitAsync(Submission(scope)).GetAwaiter().GetResult().CommandId);
            Check(ids.Distinct().Count() == scopes.Length);
            Check(store.Documents.Count == scopes.Length);
        }),
        ("Admission reauthorizes before every storage access including retries", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope) { Operator = false };
            var service = Service(store, authorizer);
            Error("forbidden", () => service.SubmitAsync(Submission()).GetAwaiter().GetResult());
            Check(store.Creates == 0 && store.Reads == 0);
            authorizer.Operator = true;
            service.SubmitAsync(Submission()).GetAwaiter().GetResult();
            authorizer.Operator = false;
            Error("forbidden", () => service.SubmitAsync(Submission()).GetAwaiter().GetResult());
            Check(store.Creates == 1 && store.Reads == 0);
            authorizer.Operator = true;
            authorizer.Generation = 8;
            Error("lease_fenced", () => service.SubmitAsync(Submission()).GetAwaiter().GetResult());
            Check(store.Creates == 1 && store.Reads == 0);
        }),
        ("Admission concurrent identical requests converge without duplicate intent", () =>
        {
            var store = new FixtureStore();
            var service = Service(store, new(Scope));
            var receipts = Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
                Task.Run(async () => await service.SubmitAsync(Submission())))).GetAwaiter().GetResult();
            Check(receipts.Select(receipt => receipt.CommandId).Distinct().Count() == 1);
            Check(receipts.Count(receipt => !receipt.Replayed) == 1 && store.Documents.Count == 1);
            Check(receipts.All(receipt => receipt.Status == CommandStatus.Accepted));
        }),
        ("Admission never replaces expired missing or malformed deduplication evidence", () =>
        {
            var store = new FixtureStore();
            var clock = new FixtureClock(Now);
            var service = Service(store, new(Scope), clock);
            service.SubmitAsync(Submission()).GetAwaiter().GetResult();
            clock.Now = Now.AddHours(1);
            Error("reconciliation_required", () => service.SubmitAsync(Submission()).GetAwaiter().GetResult());
            Check(store.Documents.Count == 1);
            clock.Now = Now;
            store.HideReads = true;
            Error("reconciliation_required", () => service.SubmitAsync(Submission()).GetAwaiter().GetResult());
            store.HideReads = false;
            var original = store.Documents.Values.Single();
            foreach (var corrupt in new Action<JsonObject>[] {
                document => document.Remove("status"),
                document => document["scope"] = null,
                document => document["schema_version"] = "2.0",
                document => document["payload_digest"] = "sha256:" + new string('b', 64),
                document => document["status"] = "unknown",
                document => document["ttl"] = 60,
                document => document["payload_reference"] = " ",
                document => document["retain_until"] = Now.AddTicks(-1).ToString("O") })
            {
                store.Documents[store.Documents.Keys.Single()] = original;
                store.Mutate(corrupt);
                Error("reconciliation_required", () => service.SubmitAsync(Submission()).GetAwaiter().GetResult());
            }
            Check(store.Documents.Count == 1);
        }),
        ("Command status exposes only authorized scoped identity and status", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope);
            var accepted = Service(store, authorizer).SubmitAsync(Submission()).GetAwaiter().GetResult();
            var statuses = new CommandStatusService(store, authorizer);
            authorizer.Operator = false;
            var receipt = statuses.GetAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId).GetAwaiter().GetResult();
            Check(receipt == new CommandStatusReceipt(accepted.CommandId, CommandStatus.Accepted));
            int reads = store.Reads;
            Error("resource_not_found", () => statuses.GetAsync("other-project", Scope.TargetId, accepted.CommandId).GetAwaiter().GetResult());
            Error("resource_not_found", () => statuses.GetAsync(Scope.ProjectId, "other-run", accepted.CommandId).GetAwaiter().GetResult());
            authorizer.ReadAllowed = false;
            Error("forbidden", () => statuses.GetAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId).GetAwaiter().GetResult());
            Check(store.Reads == reads);
        }),
        ("Runner outcome persists through restart and is replayed by admission", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope);
            var accepted = Service(store, authorizer).SubmitAsync(Submission()).GetAwaiter().GetResult();
            var statuses = new CommandStatusService(store, authorizer);
            var applied = statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId, 7,
                CommandStatus.Applied, "journal-applied").GetAwaiter().GetResult();
            Check(applied.Status == CommandStatus.Applied && store.Replaces == 1);
            var restarted = new CommandStatusService(store, authorizer);
            Check(restarted.GetAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId).GetAwaiter().GetResult() == applied);
            Check(restarted.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId, 7,
                CommandStatus.Applied, "journal-applied").GetAwaiter().GetResult() == applied);
            Check(store.Replaces == 1);
            var retry = Service(store, authorizer).SubmitAsync(Submission()).GetAwaiter().GetResult();
            Check(retry.Replayed && retry.Status == CommandStatus.Applied);
            Error("invalid_transition", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId,
                7, CommandStatus.Failed, "different-journal").GetAwaiter().GetResult());
        }),
        ("Uncertain runner outcome requires retained reconciliation before a known result", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope);
            var accepted = Service(store, authorizer).SubmitAsync(Submission()).GetAwaiter().GetResult();
            var statuses = new CommandStatusService(store, authorizer);
            statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId, 7,
                CommandStatus.Uncertain, "journal-uncertain").GetAwaiter().GetResult();
            Error("reconciliation_required", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId,
                accepted.CommandId, 7, CommandStatus.Applied, "journal-observed").GetAwaiter().GetResult());
            var resolved = statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId, 7,
                CommandStatus.Failed, "journal-not-applied", "reconciliation-1").GetAwaiter().GetResult();
            Check(resolved.Status == CommandStatus.Failed && store.Replaces == 2);
            var body = store.Documents.Values.Single();
            Check(body.GetProperty("reconciliation_reference").GetString() == "reconciliation-1");
            Check(Service(store, authorizer).SubmitAsync(Submission()).GetAwaiter().GetResult().Status == CommandStatus.Failed);
        }),
        ("Only a current authorized runner can acknowledge its bound generation", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope);
            var accepted = Service(store, authorizer).SubmitAsync(Submission()).GetAwaiter().GetResult();
            var statuses = new CommandStatusService(store, authorizer);
            authorizer.RunnerAllowed = false;
            Error("forbidden", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId,
                7, CommandStatus.Applied, "journal").GetAwaiter().GetResult());
            Check(store.Reads == 0 && store.Replaces == 0);
            authorizer.RunnerAllowed = true;
            authorizer.EvidenceCommandId = "command-" + new string('b', 64);
            Error("action_mismatch", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId,
                7, CommandStatus.Applied, "journal").GetAwaiter().GetResult());
            authorizer.EvidenceCommandId = accepted.CommandId;
            authorizer.EvidenceOutcome = CommandStatus.Failed;
            Error("action_mismatch", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId,
                7, CommandStatus.Applied, "journal").GetAwaiter().GetResult());
            Check(store.Reads == 0 && store.Replaces == 0);
            authorizer.EvidenceOutcome = CommandStatus.Applied;
            authorizer.Generation = 8;
            Error("lease_fenced", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId,
                7, CommandStatus.Applied, "journal").GetAwaiter().GetResult());
            Check(store.Reads == 0);
            Error("lease_fenced", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId,
                8, CommandStatus.Applied, "journal").GetAwaiter().GetResult());
            Check(store.Replaces == 0);
        }),
        ("Acknowledgement conflicts and ambiguous writes never overwrite or retry", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope);
            var accepted = Service(store, authorizer).SubmitAsync(Submission()).GetAwaiter().GetResult();
            var statuses = new CommandStatusService(store, authorizer);
            store.RejectReplacement = true;
            Error("version_conflict", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId,
                7, CommandStatus.Applied, "journal").GetAwaiter().GetResult());
            Check(store.Replaces == 1 && statuses.GetAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId)
                .GetAwaiter().GetResult().Status == CommandStatus.Accepted);
            store.RejectReplacement = false;
            store.FailAfterCommit = true;
            Error("durability_unavailable", () => statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId,
                7, CommandStatus.Applied, "journal").GetAwaiter().GetResult());
            Check(store.Replaces == 2);
            store.FailAfterCommit = false;
            Check(statuses.RecordOutcomeAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId, 7,
                CommandStatus.Applied, "journal").GetAwaiter().GetResult().Status == CommandStatus.Applied);
            Check(store.Replaces == 2);
        }),
        ("Status refuses missing corrupt or unsupported records without exposing payloads", () =>
        {
            var store = new FixtureStore();
            var authorizer = new FixtureAuthorizer(Scope);
            var accepted = Service(store, authorizer).SubmitAsync(Submission()).GetAwaiter().GetResult();
            var statuses = new CommandStatusService(store, authorizer);
            store.HideReads = true;
            Error("resource_not_found", () => statuses.GetAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId).GetAwaiter().GetResult());
            store.HideReads = false;
            var original = store.Documents.Values.Single();
            foreach (var corrupt in new Action<JsonObject>[] {
                document => document["scope"] = null,
                document => document["status"] = "applied",
                document => document["reconciliation_reference"] = "unbound-reconciliation",
                document => document["payload_digest"] = "sha256:" + new string('b', 64) })
            {
                store.Documents[store.Documents.Keys.Single()] = original;
                store.Mutate(corrupt);
                Error("reconciliation_required", () => statuses.GetAsync(Scope.ProjectId, Scope.TargetId, accepted.CommandId).GetAwaiter().GetResult());
            }
        }),
        ("Admission propagates durability failures without accepted receipts or retries", () =>
        {
            var store = new FixtureStore { FailWrites = true };
            Error("durability_unavailable", () => Service(store, new(Scope)).SubmitAsync(Submission()).GetAwaiter().GetResult());
            Check(store.Creates == 1 && store.Reads == 0 && store.Documents.IsEmpty);
            store.FailWrites = false;
            store.FailAfterCommit = true;
            Error("durability_unavailable", () => Service(store, new(Scope)).SubmitAsync(Submission()).GetAwaiter().GetResult());
            Check(store.Creates == 2 && store.Reads == 0 && store.Documents.Count == 1);
            store.FailAfterCommit = false;
            var replay = Service(store, new(Scope)).SubmitAsync(Submission()).GetAwaiter().GetResult();
            Check(replay.Replayed && store.Documents.Count == 1);
        })
    ];

    private static MessageAdmissionService Service(FixtureStore store, FixtureAuthorizer authorizer, FixtureClock? clock = null)
        => new(store, authorizer, clock ?? new(Now), TimeSpan.FromHours(1));

    private static MessageSubmission Submission(IdempotencyScope? scope = null, string payloadReference = "protected-payload-1",
        string? payloadDigest = null, string? memberId = null, long generation = 7, DeliveryMode mode = DeliveryMode.Immediate)
    {
        scope ??= Scope;
        var team = new PinnedTeam("team-fixture-v1", ["member-fixture"]);
        var target = new MessageTarget(team, memberId is null ? RecipientKind.Coordinator : RecipientKind.Member,
            mode, memberId);
        return new(scope, "synthetic-key", target, payloadReference, payloadDigest ?? PayloadDigest, generation);
    }

    private sealed class FixtureClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CommandTransport : HttpMessageHandler
    {
        private string? body;
        public int Creates { get; private set; }
        public int Reads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpStatusCode status;
            if (request.Method == HttpMethod.Post)
            {
                Creates++;
                if (body is null)
                {
                    body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    status = HttpStatusCode.Created;
                }
                else status = HttpStatusCode.Conflict;
            }
            else if (request.Method == HttpMethod.Get)
            {
                Reads++;
                Check(body is not null);
                var document = JsonNode.Parse(body!)!.AsObject();
                Check(request.RequestUri!.AbsolutePath.EndsWith("/" + document["id"]!.GetValue<string>(), StringComparison.Ordinal));
                status = HttpStatusCode.OK;
            }
            else throw new AssertionFailure("Admission unexpectedly attempted a command update.");
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json")
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"fixture-transport-etag\"");
            return response;
        }
    }

    private sealed class FixtureAuthorizer(IdempotencyScope allowedScope) : IMessageAdmissionAuthorizer, ICommandStatusAuthorizer
    {
        public bool Operator { get; set; } = true;
        public bool ReadAllowed { get; set; } = true;
        public bool RunnerAllowed { get; set; } = true;
        public string? EvidenceCommandId { get; set; }
        public CommandStatus? EvidenceOutcome { get; set; }
        public long Generation { get; set; } = 7;
        private int calls;
        public int Calls => calls;

        public ValueTask AuthorizeReadAsync(string projectId, string runId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (projectId != allowedScope.ProjectId || runId != allowedScope.TargetId)
                throw new DomainException("resource_not_found", "The fixture scope is inaccessible.");
            if (!ReadAllowed) throw new DomainException("forbidden", "Fixture read access is revoked.");
            return ValueTask.CompletedTask;
        }

        public ValueTask AuthorizeOutcomeAsync(string projectId, string runId, string commandId, long generation,
            CommandStatus outcome, string evidenceReference,
            string? reconciliationReference, CancellationToken cancellationToken)
        {
            AuthorizeReadAsync(projectId, runId, cancellationToken).GetAwaiter().GetResult();
            if (!RunnerAllowed) throw new DomainException("forbidden", "Runner identity required.");
            if (generation != Generation) throw new DomainException("lease_fenced", "Fixture writer is stale.");
            if ((EvidenceCommandId is not null && EvidenceCommandId != commandId) ||
                (EvidenceOutcome is not null && EvidenceOutcome != outcome))
                throw new DomainException("action_mismatch", "Fixture evidence does not bind this command outcome.");
            return ValueTask.CompletedTask;
        }

        public ValueTask AuthorizeAsync(IdempotencyScope scope, MessageTarget target, long leaseGeneration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            if (scope != allowedScope) throw new DomainException("resource_not_found", "The fixture scope is inaccessible.");
            if (!Operator) throw new DomainException("forbidden", "The fixture is read-only.");
            if (leaseGeneration != Generation) throw new DomainException("lease_fenced", "The fixture writer is stale.");
            if (target.TeamVersion != "team-fixture-v1" ||
                (target.Kind == RecipientKind.Member && target.MemberId != "member-fixture"))
                throw new DomainException("invalid_request", "The fixture team is not approved.");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureStore : IControlDocumentStore
    {
        public ConcurrentDictionary<(string Partition, string Id), JsonElement> Documents { get; } = new();
        public int Creates;
        public int Reads;
        public int Replaces;
        public bool HideReads { get; set; }
        public bool FailWrites { get; set; }
        public bool FailAfterCommit { get; set; }
        public bool RejectReplacement { get; set; }
        private readonly object gate = new();
        private long revision;

        public ValueTask<bool> CreateAsync(string partitionKey, string id, JsonElement document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Creates);
            if (FailWrites) throw new DomainException("durability_unavailable", "Fixture storage unavailable.");
            bool created = Documents.TryAdd((partitionKey, id), document.Clone());
            if (created && FailAfterCommit) throw new DomainException("durability_unavailable", "Fixture commit acknowledgement lost.");
            return ValueTask.FromResult(created);
        }

        public ValueTask<VersionedDocument?> ReadAsync(string partitionKey, string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Reads);
            return ValueTask.FromResult(!HideReads && Documents.TryGetValue((partitionKey, id), out var body)
                ? new VersionedDocument(body.Clone(), $"\"fixture-{revision}\"") : null);
        }

        public ValueTask<bool> ReplaceAsync(string partitionKey, string id, JsonElement document,
            string expectedETag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                Replaces++;
                if (RejectReplacement || expectedETag != $"\"fixture-{revision}\"" ||
                    !Documents.ContainsKey((partitionKey, id))) return ValueTask.FromResult(false);
                Documents[(partitionKey, id)] = document.Clone();
                revision++;
                if (FailAfterCommit) throw new DomainException("durability_unavailable", "Fixture update acknowledgement lost.");
                return ValueTask.FromResult(true);
            }
        }

        public void Mutate(Action<JsonObject> mutate)
        {
            var key = Documents.Keys.Single();
            var document = JsonNode.Parse(Documents[key].GetRawText())!.AsObject();
            mutate(document);
            Documents[key] = JsonSerializer.SerializeToElement(document);
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new AssertionFailure("Command admission assertion failed.");
    }

    private static void Error(string code, Action run)
    {
        try { run(); }
        catch (DomainException error) { Check(error.Code == code); return; }
        throw new AssertionFailure($"Expected command admission error {code}.");
    }
}
