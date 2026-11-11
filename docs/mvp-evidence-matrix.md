# P01 MVP evidence matrix

Status: **draft, pending team review; every capability below is UNPROVEN**. Documentation is not execution evidence or contract approval.
Basis: [PRD](../prd.md), [TDD](../tdd.md), [proposal](../mvp-implementation-proposal.md), [draft contracts](./mvp-contracts.md), [workflow](../.squad/workflow.md).
Evidence IDs below are proposed retained artifact bundles, not existing tests/results. Each must link immutable inputs, versions, raw results, failures, timestamps and reviewer disposition.
Owners produce/reproduce evidence; Livingston independently verifies unless he authored it, when Steve assigns another qualified verifier. Linus reviews all security-sensitive changes; no self-review.
VG numbers refer to the unchanged TDD validation backlog; all listed gates remain open. Local document/link checks close none of them.

## M1 functional acceptance

| Requirement / M1 portion | Specific required evidence bundle | Accountable owner | Current status / blockers |
|---|---|---|---|
| FR-01 registration/activation | E01: valid activation plus missing-config/unauthorized cases; safe names/no secret values; persisted approved repository/team/telemetry references | Reuben | UNPROVEN; VG-01/10, approved inputs absent |
| FR-02 project/run isolation | E02: real per-run sandbox mapping; second-project fixture denies files, credentials, runner/stream/artifact access without disclosure | Basher | UNPROVEN; VG-01/03 |
| FR-03 pinned startup | E03: signed digest/snapshot provenance; recorded team/config/compatibility inventory; tampered/unapproved digest refusal | Basher | UNPROVEN; VG-01/02/04/10 |
| FR-04 single writer/claims | E04: concurrent starts, CAS renewal/expiry, stale-generation writes/ref claims; successor stays unready until old in-flight process termination and reconciliation proved | Reuben | UNPROVEN; VG-01/02/04/09 |
| FR-05 lifecycle | E05: real cancel/pause/resume/stop traces during active tool/input; no active-turn auto-suspend; restart and incomplete finalization yield recovery, never false completion | Reuben | UNPROVEN; VG-02/04/12 |
| FR-06 targeted delivery | E06: coordinator/monitor/member immediate and ordered queue runs during long tool; accepted/applied/failed/uncertain receipts survive restart without blind side-effect replay | Rusty | UNPROVEN; VG-04 |
| FR-07 human decisions | E07: two authorized answers plus timeout race; one durable winner/one execution; permission denial at 300 seconds across restart; mismatched/stale action rejection; question/plan expiry and UI withdrawal | Reuben | UNPROVEN; VG-04/05 |
| FR-08 console/replay | E08: conversation/member/lifecycle/approval/filterable-log demonstration; cursor reconnect, source dedup, snapshot boundary, explicit gaps and slow-client resync | Saul | UNPROVEN; VG-04/06 |
| FR-09 project roles | E09: every read/write/stream/reconnect endpoint role matrix; Viewer mutation, cross-project and expired-token denials; no editable attach surface | Reuben | UNPROVEN; VG-01/03; identity bindings needed |
| FR-10 tool enforcement | E10: protected versioned settings/hook, unknown/error/timeout deny backstop, bypass/path/child-process adversarial results on real tools | Linus | UNPROVEN; VG-04/05 |
| FR-11 egress/credentials | E11: image/files/env/metadata inspection; unapproved and unrelated-GitHub destination denial; external injection/rotation; snapshot trust refresh with no open-egress interval | Linus | UNPROVEN; VG-01/02/03/09 |
| FR-12 complete audit | E12: full source inventory, stable IDs, append-timeout retry, independent source watermarks/exports, missing-source fault, signed manifest plus independent verifier output | Reuben | UNPROVEN; VG-06/12; hashes alone insufficient |
| FR-13 dual telemetry | E13: CLI/orchestration/runner/governance/platform/control-plane coverage map; same correlated log in console and destination; redaction, collector/destination outages and explicit gaps | Livingston | UNPROVEN; VG-06; separate verifier required |
| FR-14 minimal PR path | E14: isolated branch/worktree task -> reviewable PR with run/evidence links; protection/human-merge denial tests; any introduced webhook signature/scope/duplicate tests | Reuben | UNPROVEN; VG-03/09/12 |
| FR-15 versioned M1 API | E15: request/command/event conformance; concurrent/restarted identical-key retries converge; payload mismatch and scope errors; approval duplicates; authenticated SSE replay/lifecycle notifications | Reuben | UNPROVEN; VG-04/12; contract review pending |
| FR-22 release evaluation | E22: versioned scenarios/rubrics, baseline/candidate repeats, model/runtime/config/scorer provenance; agreed numerical thresholds and intentionally failing regression blocks promotion; independent disposition | Livingston | UNPROVEN; VG-04/10/13; thresholds/access not approved |

## Applicable non-functional acceptance

| Requirement / scope | Specific required evidence bundle | Accountable owner | Current status / blockers |
|---|---|---|---|
| NFR-01 security | E-N01: E10/E11/E14 plus agreed adversarial suite with zero bypasses and zero long-lived run credentials; retained negative findings/fixes | Linus | UNPROVEN; VG-03/05/09 |
| NFR-02 isolation | E-N02: E02/E04/E09 negative matrix across projects/runs, streams/artifacts/writable state; disabled external memory/attach verified unavailable | Linus | UNPROVEN; VG-01/03/04 |
| NFR-03 reliability | E-N03: separate control-plane/runner restart, suspension, lease loss and ambiguous SDK crash injections; durable state reconstruction, uncertain status, no second execution/writer | Rusty | UNPROVEN; VG-02/04/12 |
| NFR-04 performance | E-N04: approved samples/warm-cold/concurrency/region/version/clocks; submit->session ack p95 <3s, producer->both log destinations <10s, accepted start->snapshot readiness <60s; include errors/timeouts | Livingston | UNPROVEN; VG-02/04/06/13; targets not SLOs |
| NFR-06 audit durability | E-N06: E12 over all tested completed runs (100% complete/verified); missing-source/outbox-full/append-ambiguity drills; pilot retention/residency disposition | Reuben | UNPROVEN; VG-06/12; no production lock |
| NFR-07 privacy | E-N07: default content-capture-off/redaction tests; protected identity lookup and evidence/artifact authorization; secret/PII scans of retained logs/evaluations | Linus | UNPROVEN; VG-03/06/12; privacy policy needed |
| NFR-08 supply chain | E-N08: approved-feed-only full graph lock/SBOM/provenance/publisher/digest/original publication evidence; strictly >7-day age at selection; missing/unknown/new/public-fallback refusal | Linus | UNPROVEN; VG-10; installs blocked without inputs |
| NFR-09 developer workflow | E-N09: Python 3.13 Apple Silicon local-venv bootstrap/check output, protected configuration precedence/exclusion; Intel differences; remote build without local Linux daemon | Basher | UNPROVEN; VG-01/10 |
| NFR-10 accessibility | E-N10: keyboard/screen-reader approval, steering/lifecycle and live-status tests; non-color state cues; WCAG 2.2 AA findings and remediation | Saul | UNPROVEN; UI absent; VG-04 capability dependencies |
| NFR-11 operability | E-N11: readiness plus stuck-approval/policy/egress/audit/heartbeat alerts; bounded cleanup/recovery/rollback and authorized evidence retrieval drills; pilot limit and honest unavailable-cost display | Livingston | UNPROVEN; VG-02/03/06/12/13; separate verifier |
| NFR-12 maintainability | E-N12: pinned compatibility conformance and feature-flag inventory; upgrade/rollback rehearsal for CLI/SDK/team/contracts; AHP/ACP remain disabled | Rusty | UNPROVEN; VG-04; VG-07/08 deferred, not passed |
| NFR-13 evaluation integrity | E-N13: E22 reproducibility bundle separates correctness/quality/latency/cost, records scenario/dataset/model/runtime/config/scorer/result versions and variability; no invented aggregate score | Livingston | UNPROVEN; VG-10/13; separate verifier |

## Exclusions, gate disposition and independent handoff

- M2: FR-16 workspace attach, FR-17/18/19 MCP/memory, optional shared-session protocols and full integration/lifecycle webhook products. FR-15's M1 control/API surface remains required.
- M2/M3: FR-20 team portability/sprint automation; FR-14's broader integrations follow later, but M1 PR protections/signature checks above are not deferred.
- M3: FR-21 factory cost/quotas, NFR-05 five-project/50-run scale and FR-23 unattended mode. A bounded pilot limit and honest unknown usage remain required.
- VG-01/02: Basher proves current regional/network/API, lifecycle and process/snapshot feasibility using retained real-platform probes.
- VG-03/05/09: Linus proves egress/identity/permission/GitHub support; Rusty supplies execution traces. No borrowed tokens or approve-all workaround.
- VG-04: Rusty proves exact pinned CLI/SDK/Squad callbacks, routing, cancellation and persistence; direct composition is not presumed safe without proof.
- VG-06/12: Reuben owns audit durability/completeness/signing; Livingston supplies producer coverage. No source receipt/completeness guarantee means no complete-run claim.
- VG-10: Linus must establish approved feed/authenticity/age evidence before any restore/build promotion.
- VG-13: Steve obtains product/platform dispositions for benchmark parameters, thresholds, recovery objectives and budget with Livingston.
- VG-07/08/11 remain deferred while client/memory features are disabled; no current-support claim or implicit M1 dependency is made.

For each bundle, retain an evidence URI/digest, exact test/scenario revision, environment/compatibility inventory, raw results including failures, accountable owner, independent verifier and domain-review record.
Local document checks establish only readable links and draft coverage, not functional correctness, Azure availability or completed gates.
Follow `ready -> claimed -> implemented -> verified -> reviewed -> done`; draft P01 is not `verified`, `reviewed` or `done`.
Steve chairs contract/scope review; Livingston verifies independently; Linus reviews security-sensitive changes; Steve assigns independent review/verification for their own authored work.
Fact Checker checks capability/source claims, Rai reviews human/approval/privacy risks, and Scribe records only actually reviewed decisions. No author alone signs off a claim.
P16/P17 require real-sandbox acceptance and retained evidence for every applicable row, not mocks or document status. Missing governance, writer safety, source completeness or agreed quality thresholds blocks release.
