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
python3.13 tools/check_core.py
```

The Python checks cover preflight and offline-build orchestration. The second
command builds and runs the dependency-free C# behavioral test executable.
There is deliberately no external test framework while package eligibility and
authenticated build feeds remain unresolved.

The core projects declare no third-party package dependencies. The validation
script uses a temporary NuGet configuration with no package sources, isolated
empty package/cache directories and a minimal child environment. An offline
`dotnet restore` generates project metadata from installed SDK/reference packs;
it cannot obtain a missing package or reference pack from an approved or public
feed. Subsequent build/run commands prohibit implicit restore. Missing tools,
packs, unsupported SDK versions and failed checks return nonzero.

Only the local, no-package core is supported by this workflow. The script is
not a sandbox for untrusted MSBuild projects and does not establish SDK publisher,
provenance, age, vulnerability or production dependency-eligibility evidence.
Dependency declarations require a separately reviewed approved-feed workflow,
not adding fallback sources to this script. NuGet network auditing is disabled
for this isolated no-package check, not for future production dependency builds.

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

The no-package Release build and all 13 C# behavioral cases pass locally.
The 22 Python tests cover preflight and offline validation orchestration.
These results verify local rules only. Durable approval CAS, project
authorization, journal/outbox persistence, lease enforcement, SDK dispatch and
real audit verification remain integration work.

Regression cases cover combined enum names that could alias another recipient
and attempted finalization without work settlement. Protocol enum decoding
accepts only exact supported snake-case names. Evidence references are still
caller-attested; production adapters must independently verify their meaning.

Independent Linus re-review confirmed both findings resolved, including the
original failing probes, with no remaining blockers for the fixes. This is
bounded code-review evidence, not platform or MVP release approval.

The installed SDK also emits a workload-verification diagnostic before restore.
The core uses no optional workloads and the subsequent build succeeds with zero
build warnings/errors. No workload update or installation is attempted to hide
that diagnostic; deployment prerequisites remain separate.
