# Offline core development

The first coding slice implements pure .NET 10 control-domain rules and Python
validation automation. It is not a deployed control plane, durable store, runner,
SDK integration or proof of sandbox governance. The [contract baseline](./mvp-contracts.md)
remains a reviewable draft; [MVP acceptance](./mvp-evidence-matrix.md) is not implied.

## Local checks

Use an existing Python 3.13 installation and a selected stable .NET 10 SDK with
its reference/runtime packs already installed:

```sh
python3.13 -m unittest discover -s tests -p 'test_*.py'
dotnet build tests/RemoteSquads.Core.Tests/RemoteSquads.Core.Tests.csproj --configuration Release --no-restore
dotnet run --project tests/RemoteSquads.Core.Tests/RemoteSquads.Core.Tests.csproj --configuration Release --no-build --no-restore
```

The Python checks cover preflight and offline-build orchestration. The .NET
commands build and run the dependency-free C# behavioral test executable using
existing restore assets, without a package restore or auditing overrides.
Missing assets or packs block this path; do not install or add feeds to bypass
the dependency eligibility gate.
There is deliberately no external test framework while package eligibility and
authenticated build feeds remain unresolved.

The test executable also references the control-plane endpoint library and
installed ASP.NET Core shared framework. HTTP integration cases start only
temporary loopback Kestrel hosts and dispose them after actual requests; they
do not start an enduring development or production service.

The core projects declare no third-party package dependencies. The original
`tools/check_core.py` helper uses a temporary NuGet configuration with no package sources, isolated
empty package/cache directories and a minimal child environment. An offline
`dotnet restore` generates project metadata from installed SDK/reference packs;
it cannot obtain a missing package or reference pack from an approved or public
feed. Subsequent build/run commands prohibit implicit restore. Missing tools,
packs, unsupported SDK versions and failed checks return nonzero.

Only the local, no-package core is supported by this workflow. The script is
not a sandbox for untrusted MSBuild projects and does not establish SDK publisher,
provenance, age, vulnerability or production dependency-eligibility evidence.
Dependency declarations require a separately reviewed approved-feed workflow,
not adding fallback sources to this script. The helper disables NuGet network
auditing for its isolated no-package restore; executing it was denied and it
has not been retried. The supported validation above does not use that option
and does not verify a fresh restore.

No command reads the private `.env`, contacts Azure, creates resources, starts
an agent/model turn or grants approval to deploy. CFS default proxy values in
the template remain corporate developer-machine inputs, not CI/remote-build
sources. Review [the feed restrictions](./preflight.md#cfs-developer-feed-defaults).

## Domain boundaries

Run lifecycle, delivery and approval rules are local domain decisions. Production
adapters must authorize project scope, use durable conditional writes, persist
command intent/evidence before dispatch, reconcile uncertain side effects and
independently verify final manifests before completing a run.

A passing local aggregate test does not prove cross-process approval atomicity,
writer fencing, audit completeness, SDK compatibility or platform isolation.
No in-memory domain object may be substituted for the required durable control
plane and called production-ready.

## Implemented coding slice

- Validated coordinator/work-monitor/member recipients and delivery modes,
  including pinned team membership and strict protocol enum parsing.
- Command outcomes with terminal-result protection and explicit reconciliation
  evidence for uncertain delivery.
- Immutable permission decisions bound to action, policy and lease generation,
  conditional version checks, stable matching retries and denial at the exact
  300-second expiry boundary.
- Separate lifecycle steps for stopping and finalization; suspension, readiness,
  resume and completion require their respective evidence. Finalization also
  requires referenced evidence that turns, tools, pending operations and
  approvals have settled.

The no-package Release build and all 52 C# behavioral cases pass locally.
The 22 Python tests cover preflight and offline validation orchestration.
These results verify local rules only. Durable approval CAS, production project
authorization, journal/outbox persistence, lease enforcement, SDK dispatch and
real audit verification remain integration work.

Regression cases cover combined enum names that could alias another recipient
and attempted finalization without work settlement. Protocol enum decoding
accepts only exact supported snake-case names. Evidence references are still
caller-attested; production adapters must independently verify their meaning.

Independent Linus re-review confirmed both findings resolved, including the
original failing probes, with no remaining blockers for the fixes. This is
bounded code-review evidence, not platform or MVP release approval.

## Control-plane safety primitives

The next offline slice adds immutable candidates for the P09 control aggregates:

- Team writer leases scoped by project/team, conditional version checks,
  monotonically increasing generations, renewal and exact-deadline fencing.
  Expiry alone cannot admit a successor: referenced settlement must bind the
  previous run/generation and attest process containment and side-effect
  reconciliation, including when the same run reacquires a lease.
- Idempotency bindings scoped by authenticated subject/client, project,
  operation, target and key. Matching semantic SHA-256 fingerprints and schema
  major versions replay the original result reference; mismatches fail.
  Expired retry windows require reconciliation rather than fresh dispatch.

These are **not durable locks, authorization, payload canonicalization or
exactly-once execution**. Production adapters must derive identity from
authenticated context, reauthorize retries, hash validated canonical semantic
payloads without transport-only IDs, and atomically persist bindings with
command intent/results. Keep expired bindings as tombstones; missing retry
evidence must fail closed rather than create a replacement command.

Lease adapters must use authoritative UTC time and durable CAS, verify settlement
against real process/repository evidence, and check the current persisted
generation on every dispatch/write. A stale in-memory snapshot cannot establish
current ownership. These local rules do not close VG-04 or prove in-flight
process fencing. Technical contract approval and real-store validation remain open.

Five additional behavioral cases exercise competing writers, stale versions,
expiry/takeover, renewal, scope separation, fingerprint mismatches and invalid
time/retention boundaries.

Earlier primitive-slice validation on 2026-11-12:

- Independent execution passed all 22 Python tests.
- The standard offline check script was denied because it disables NuGet
  auditing. It was not retried; fresh offline restore was not verified.
- A build using existing restore assets, without restore or auditing overrides,
  succeeded with zero warnings/errors:
  `dotnet build tests/RemoteSquads.Core.Tests/RemoteSquads.Core.Tests.csproj --configuration Release --no-restore`.
- Independent execution of
  `dotnet run --project tests/RemoteSquads.Core.Tests/RemoteSquads.Core.Tests.csproj --configuration Release --no-build --no-restore`
  passed all 18 behavioral cases, including all five new cases.
- Independent Linus-domain review found no significant issues within the
  immutable aggregate boundary. This is bounded code review, not accepted
  contracts, production dependency eligibility or release approval.

The installed SDK also emits a workload-verification diagnostic before restore.
The core uses no optional workloads and the subsequent build succeeds with zero
build warnings/errors. No workload update or installation is attempted to hide
that diagnostic; deployment prerequisites remain separate.

## Command admission and persistence integration

`MessageAdmissionService` composes authorization and storage for the M1 message
path. It requires an `IMessageAdmissionAuthorizer`; there is no default permit
implementation. A future authenticated HTTP host must derive subject/client
identity from authenticated context and resolve project/run ownership, Operator
role, pinned team and current writer generation before each attempt, including
retries. No HTTP listener or identity-provider integration is implemented here.

Admission stores the idempotency binding and accepted command intent in one
create-only document. The ID hashes subject/client, project, operation, run and
key; the partition hashes project/run. Both delivery modes use the same
`message` operation scope, so changing mode with the same key is a payload
mismatch, not a second command. A fixed-shape UTF-8 JSON fingerprint
binds team version, recipient, mode, protected payload digest and writer
generation. The protected payload reference is not semantic content: equivalent
payload references with the same digest replay the original intent.

A conflicting create reads the original document and returns its current
command ID/status only after scope, schema, fingerprint and retry-window
validation. Missing, malformed or expired evidence requires reconciliation.
An ambiguous commit does not return acceptance or blindly retry dispatch;
another submission with the same key can reconcile through create conflict.
Admission never updates/replaces an existing command or executes an SDK/tool.
An accepted receipt is not an applied steering operation.

`IControlDocumentStore` separates create, point read and ETag-conditional
replacement. The Cosmos REST adapter must use Entra authorization, a reviewed
HTTPS endpoint and an existing database/container; no provisioning or token
acquisition is implied. Its container partition-key path is `/partition_key`.
Command records use `ttl: -1`; retention/deletion policy must preserve bindings
as tombstones, not permit key reuse. A reviewed single-write-region account,
consistency configuration, private networking, data-plane RBAC, backup/recovery
and actual conditional-write behavior still need real-cloud verification.

`CosmosControlDocumentStore` accepts an account-root URI, database/container
IDs and a `Func<CancellationToken, ValueTask<string>>` token provider. Only
commercial Azure account endpoints (`https://<account>.documents.azure.com/`)
are supported. Reads explicitly request Strong consistency; a weaker account
must fail rather than silently serve stale deduplication evidence. Verify
single-write-region topology and disabled container TTL externally.

The owned transport disables redirects/cookies. The optional handler is a
trusted test seam, not user-supplied runtime configuration. Requests default to
a 30-second deadline (configurable up to two minutes); responses are capped at
2 MiB. The deadline also bounds token acquisition. Caller cancellation
propagates, errors do not echo tokens/bodies, and the adapter performs no
automatic retries. Dispose the adapter after use. This is a control-plane
integration; it does not authorize placing credentials in run sandboxes.

Authorizer fixtures and fake document/HTTP stores test local composition only.
They do not prove authentication, server durability, real cross-process races,
in-flight fencing, audit completeness or SDK execution. Dispatch must recheck
current persisted lease/policy and durably record required audit evidence.
Production retention/residency and technical contract approval remain gates.

The transport follows Microsoft's documented REST
[create](https://learn.microsoft.com/rest/api/cosmos-db/create-a-document),
[point-read](https://learn.microsoft.com/rest/api/cosmos-db/get-a-document),
[conditional replace](https://learn.microsoft.com/rest/api/cosmos-db/replace-a-document),
[request headers](https://learn.microsoft.com/rest/api/cosmos-db/common-cosmosdb-rest-request-headers)
and [Entra authorization](https://learn.microsoft.com/rest/api/cosmos-db/access-control-on-cosmosdb-resources).
Documentation establishes the intended protocol, not compatibility with a
provisioned account or a passed capability gate.

Command-integration validation on 2026-11-12:

- Independent no-restore Release build: zero warnings/errors.
- Independent C# execution: 38/38 passed (18 existing domain cases, nine
  admission cases and 11 REST transport cases). This includes actual service
  and adapter composition against a fake HTTP server, concurrent same-key
  admission, commit-acknowledgement loss, replay of uncertain status,
  reauthorization and corrupt/expired binding refusal.
- Independent Python execution: 22/22 passed.
- Independent Linus-domain review of service, adapter and composed-test delta:
  no significant issues within the stated offline boundary.
- No dependencies installed, fresh restore attempted, cloud contacted or
  resources provisioned. The earlier helper-script denial remains unchanged.

P09 is still partial. Production HTTP authentication/role bindings, real Cosmos
deployment/probes, durable permission/lease integration, audit-gated dispatch,
runner execution and release evidence remain work; passing synthetic
transport tests does not close those gates.

## Command status and runner outcomes

`CommandStatusService` reads admitted commands by project/run/command ID and
returns only the ID and delivery status, without payload references, actor
references, keys or journal evidence. `ICommandStatusAuthorizer` is mandatory:
it resolves current scoped read access before storage access. Reading status
does not require permission to submit messages, a current writer lease, or an
unexpired idempotency retry window.

The same service conditionally records runner observations using the stored
ETag. Acknowledgements require a separate runner authorization check, the
command's writer generation and a retained outcome evidence reference.
Matching retries return the persisted result without writing again; conflicting
terminal outcomes cannot replace it. Uncertain outcomes can become applied or
failed only with an additional reconciliation reference. CAS conflict and
ambiguous write failures are surfaced, never blindly retried.

Admission replay and status retrieval share validation of the stored intent,
scope, deterministic identity and evidence. Non-accepted states without an
outcome reference are rejected as requiring reconciliation; older unsupported
outcome records need an explicit evidence-backed migration, not invented
references. These changes do not discard bindings or shorten their retention.

The authorizer implementations remain integration responsibilities, not
permit-all defaults. A production runner authorizer must verify authenticated
runner identity, current persisted lease and actual journal/reconciliation
evidence bound to the exact command ID and requested outcome (both supplied
to outcome authorization). The interface and synthetic fixtures do not prove any of those
checks. ETag CAS protects the command aggregate only, not a simultaneous
lease change or SDK/tool execution. A status acknowledgement is not audit
completion, an execution permission or an instruction to dispatch again.

Six additional cases cover authorized reads, admission-to-outcome-to-replay
across service recreation, terminal immutability, uncertain reconciliation,
runner/generation denial, CAS conflict, lost update acknowledgements and
malformed stored evidence. No HTTP endpoint, token validation, actual Copilot
adapter or real-store deployment is included in this slice.

Status-slice validation on 2026-11-12:

- Independent no-restore Release build passed with zero warnings/errors;
  independent execution passed 44/44 C# cases.
- Independent review identified that outcome authorization needed the command
  ID and requested outcome to bind evidence correctly. Both are now supplied;
  regression checks reject evidence for another command/result before storage
  access. Independent targeted re-review and execution confirmed the fix.
- No dependencies, restores, cloud operations or restricted role/history reads
  were used. Python tooling was unchanged; its last independent result remains
  22/22, not a new execution claim.

## HTTP command boundary

`src/RemoteSquads.ControlPlane` is an ASP.NET Core endpoint library using only
the installed `Microsoft.AspNetCore.App` shared framework, not new NuGet
packages. `MapSquadCommands(authenticationScheme)` maps:

- `POST /v1/runs/{runId}/messages`: versioned message envelope plus
  `Idempotency-Key`; returns HTTP 202, the durable command ID/status, `Location`
  and `status_url`. Retries preserve existing admission semantics.
- `GET /v1/runs/{runId}/commands/{commandId}`: returns the scoped ID/status,
  never content, actor identity, keys or evidence references.

Each request explicitly authenticates through the host's named scheme.
There is no permissive/default handler. A production token-validation handler
must validate signature, trusted issuer, audience, lifetime and authorized
client policy, and expose one authenticated identity with unmapped `sub`,
`azp` and `exp` claims. The boundary additionally rejects missing, duplicate
or expired identity claims and expired authentication tickets. It never trusts
identity fields in the request body or caller-supplied identity headers.

`IProjectRunAccessResolver` resolves ownership and current project roles from
server-side state before command storage or body processing. Viewer can read;
Operator and Administrator can submit. Missing/inaccessible runs must return
non-disclosing `resource_not_found`. The admission authorizer still rechecks
current scope, role, pinned team and writer generation after payload storage.
No project role grants runner-outcome authority: no public outcome route exists.

The host must supply an approved `IProtectedMessagePayloadStore` before
persisting text, including privacy/retention/access controls. The boundary
checks its reference and SHA-256 digest against the exact UTF-8 message text.
Payload storage and command admission are separate transactions; deterministic
payload storage and authorized orphan reconciliation/retention are required.
There is no content-storage fallback. HTTP bodies are bounded to 64 KiB
including bodies without Content-Length. Invalid enums, malformed/duplicate
JSON, mismatched scope and unsupported schema majors fail explicitly.
Responses disable caching; problem responses and endpoint rejection logs do
not include dependency exception text or message content.

The host must register authentication, `TimeProvider`, the resolver, protected
payload sink, `IControlDocumentStore` and `MessageAdmissionService` with its
real authorizer. It remains responsible for TLS/private ingress, request
deadlines/rate limits, safe transport/access logging, dependency readiness
and production exception handling. This library does not select or download
an identity package, bootstrap credentials or launch a production service.
Real identity configuration, server-side role/lease adapters, real Cosmos
validation and Copilot runner integration remain open.

Adding the shared-framework project required new project assets. After the
expected missing-assets build failure, a restore with **no package sources**,
isolated package directory and `NuGetAudit=true` succeeded using installed
SDK/reference packs. No packages were downloaded or feeds approved; the
earlier audit-disable helper was not rerun. Future fresh restores must retain
these restrictions until an approved-feed workflow is available.

HTTP-slice validation on 2026-11-12:

- Independent Release build: zero warnings/errors; independent C# execution:
  **52/52 passed**, including eight actual loopback HTTP integration cases.
- The HTTP cases cover absent/expired/ambiguous identities, Operator admission
  and Viewer read-only access, same-key replay and mismatch, cross-run denial
  before storage, malformed/unsupported/oversized bodies, redacted errors,
  admission reauthorization, spoofed actor/role fields and null/duplicate fields.
- Independent source and test review found no significant issues within this
  endpoint-library boundary.
- Python validation: 22/22 passed after the project-manifest inventory update.
- Test hosts bound dynamically to `127.0.0.1:0`, answered real requests and
  stopped/disposed after each case. Synthetic authentication, roles and stores
  exist only in tests; no production fake handler or fallback is registered.

These are real local HTTP routing/authorization-composition tests, not evidence
of real Entra token validation, production role persistence, Cosmos durability
or SDK execution. The next integration gate is production host composition
and authoritative runner delivery, not a claim that the MVP is complete.

## Journaled runner delivery integration

The bounded offline runner slice composes `MessageAdmissionService`,
`RunnerDeliveryService` and `CommandStatusService` through the same
`IControlDocumentStore`. It introduces no project, package, restore or host.
Danny selected this milestone; Rusty accepted implementation ownership.
Steve's pre-implementation contract review was conditional on the safety
rules below, not production or release approval.

- A server-derived `RunnerSession` pins project, run, session and writer
  generation. Required `IRunnerDeliveryAuthority` checks authenticate before
  storage and recheck the persisted writer, policy and pinned recipient before
  dispatch. There is no permissive/default implementation.
- One per-run CAS journal retains ordered `started`, `observed` and `settled`
  events. A committed start precedes every adapter call. Ambiguous writes,
  failed writes and CAS losers never authorize dispatch. Only a known delivery
  outcome copied to command status and subsequently settled admits another
  command. Duplicates reuse retained evidence, not the SDK.
- Acknowledgements bind session, generation, command, attempt and sequence.
  `applied` means observed message delivery, **not completed work**.
  Adapter exceptions, cancellation or invalid acknowledgement binding become
  `uncertain`, blocking subsequent dispatch.
- Explicit recovery requires additional authority attesting previous dispatch
  process containment. It never calls the SDK. Abandoned starts become
  uncertain; durable observations retry status/settlement only. CAS prevents a
  late writer from replacing recovery evidence, but cannot stop external tools.
- Caller cancellation is cooperative through the adapter token, not a
  cancel-current-turn command or proof of process/tool interruption. Durable
  bookkeeping uses a separate host-selected cleanup budget, at most 30 seconds.
  Cleanup failure leaves blocking evidence; a persisted valid acknowledgement
  is never downgraded because of later cancellation.
- The journal hard-stops at 32 commands / 128 KiB and reserves 16 KiB before
  dispatch for observation and settlement. It does not expire or discard
  records (`ttl=-1`). This is a bounded integration demonstrator, not an
  archival/retention policy or scalable event service. Full journals, uncertain
  entries and session/generation replacement require a subsequent reviewed
  reconciliation/archival contract; changing command status alone does not
  unblock an uncertain journal.

`UnavailableRunnerSessionAdapter` explicitly throws `sdk_unavailable`; there
is no successful fake production adapter. Tests inject a synthetic adapter
and identity/lease/policy/evidence resolver. The real host must supply protected
payload resolution and digest verification, authenticated current-lease/policy
adapters, verified journal evidence authorization, process containment and a
validated SDK adapter. No SDK/platform capability is inferred from fake tests.

Implementer validation on 2026-11-12: the prescribed no-restore Release build
passed with zero warnings/errors; **71/71 C# cases passed**, including 19 new
runner integration cases and the existing eight real loopback HTTP cases.
The first execution exposed a concurrent replay read-order race (70/71);
reloading status after observing a settled journal fixed it before the passing
rerun. Cases cover composed admission/delivery/status/replay, concurrent
authority, pre/post-dispatch cancellation, lost acknowledgements, corrupt
bindings, ambiguous storage commits, recovery, status/settlement failures,
capacity and lease loss. Independent verification and security review are
recorded separately when actually completed.

Independent close-out evidence on 2026-11-12:

- Livingston independently ran
  `dotnet build tests/RemoteSquads.Core.Tests/RemoteSquads.Core.Tests.csproj --configuration Release --no-restore`:
  exit 0, zero warnings/errors.
- Livingston ran
  `dotnet run --project tests/RemoteSquads.Core.Tests/RemoteSquads.Core.Tests.csproj --configuration Release --no-build --no-restore`
  three times: each exit 0, **71/71 passed**, zero failures.
- Livingston ran
  `PYTHONDONTWRITEBYTECODE=1 python3.13 -m unittest discover -s tests -p 'test_*.py'`:
  exit 0, **22/22 passed**. All check processes exited; no enduring hosts
  were launched.
- Livingston gave bounded local verification sign-off with no blocking
  quality defects. Linus independently reviewed the new source/tests and
  existing contracts read-only and gave bounded security-domain sign-off:
  no significant issues against this offline contract. Linus did not execute
  tests; the execution evidence belongs to Livingston.
- The coordinator records these exact returned findings here as the bounded
  delivery handoff, not invented member conversations or release approval.
  Existing `.squad` changes were preserved, not modified. The missing
  `.squad/agents/danny/history.md` was reported. No authoritative
  `.squad/decisions.md` completion entry was written in this cycle, so this
  report does not certify the workflow's formal `done` gate.

No downloads, restores, cloud operations, commits or pushes occurred in this
slice. Approved-feed/provenance evidence still blocks real SDK installation;
real Entra/roles/lease/store adapters and platform fencing remain unverified.
Cloud work remains blocked by missing resource group and spend cap.
