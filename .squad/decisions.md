# Squad Decisions

## Casting baseline repair

- **Decision:** Archive the inconsistent legacy casting pair and establish a new
  managed baseline from the current roster.
- **Authorization:** The user explicitly selected this repair instead of waiting
  for a matching historical backup.
- **Reason:** The legacy history references identities absent from the current
  registry; the installed SDK's migration rejected those unknown references.
- **Preservation:** All 13 current registered identities, including Danny, retain
  their names, universes, statuses and recorded creation times. The exact original
  registry/history pair is retained in
  [the repair archive](./casting/baseline-repair-archive-c5da70c3358ebd13092b9d6c28af838891c1c97c80fc17b17938966e731ce772/).
- **Semantics:** Revision 1 is a fresh managed baseline, not a reconstruction of
  past assignments. The active assignment/usage history starts empty; the old
  evidence remains archived.
- **Validation:** The installed Squad SDK transaction writer committed and read
  back the pair; `squad health --json` passed team, registry/charters and routing
  checks. This validates team configuration only, not MVP implementation or
  production readiness.

## Offline control-plane integration check-in

- **Authorization:** The user requested check-in and PR merge and confirmed
  that all current changes are in scope.
- **Scope:** Preserve the pending Squad configuration/casting repair and
  documentation alongside the offline lease, idempotency, admission, Cosmos
  transport, HTTP boundary, command status and journaled runner integration.
- **Review evidence:** [The development handoff](../docs/development.md#journaled-runner-delivery-integration)
  records Livingston's independent 71/71 C# and 22/22 Python verification
  and Linus's bounded security-domain sign-off. These are existing recorded
  findings, not new reviews performed by the coordinator.
- **Check-in validation (2026-11-13):** Python 3.13 unittest discovery passed
  22/22; the no-restore Release build passed with zero warnings/errors; the
  no-build/no-restore C# executable passed 71/71; `git diff --check` passed.
- **Boundary:** This handoff is for the bounded offline integration, not MVP
  completion, production readiness, dependency approval or deployment.
  Real identity, role/lease/store adapters, SDK execution and platform fencing
  remain unverified. The previously reported missing Danny history remains
  unchanged. No dependencies were installed and no restore or cloud operation
  was performed for this check-in.
