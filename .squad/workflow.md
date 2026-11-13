# Squad Workflow & Ceremonies

## The ready → claimed → implemented → verified → reviewed → done workflow

| Stage | Meaning | Who acts |
|-------|---------|----------|
| **ready** | Work is dependency-ready — all prerequisite work is `done` and the item is unblocked. | Ralph surfaces ready items from the backlog; it dispatches only dependency-ready work and never grants approvals. |
| **claimed** | A single accountable owner has taken the item per the routing table (or Steve, for ambiguous/cross-component work). | The owning implementer claims it. |
| **implemented** | The owning agent has completed the change in isolation, with evidence (diffs, logs, test output, What-If plans) attached. | The implementing agent. |
| **verified** | Independent, evidence-based confirmation that the change actually works — the reviewer here is never the implementer. | Livingston verifies quality/evaluation/acceptance claims independently; for infra changes this includes Basher's What-If diff as evidence. |
| **reviewed** | Review by the named reviewer for the change's domain. | The owner's designated reviewer, and **always** Linus for any security-sensitive change, per routing rule "Linus reviews security-sensitive changes." |
| **done** | All prior gates passed; Scribe has recorded the decision/evidence trail. | Scribe records the decision, evidence, and handoff into `.squad/decisions.md`. |

Isolated work, independent review, and evidence-based completion are non-negotiable: the implementer never verifies or reviews their own change, and no item moves to `done` without attached evidence (logs, test/evaluation output, infra What-If diffs, or security review notes).

## Ceremonies

- **Planning** — chaired by Danny; sets program priorities, milestone scope and accountable owners with Steve and component owners. Steve owns technical sequencing, feasibility and engineering decomposition.
- **Scope and decision review** — chaired by Danny; records options, recommendation, accountable decision owner, scope/acceptance impact and disposition. Danny resolves delivery trade-offs within approved scope; Steve decides technical architecture/contracts. Material scope changes, spending, deployment authorization, privacy/retention policy and release approval go to the human sponsor; no governance or evidence gate is waived.
- **Contract review** — chaired by Steve; reviews cross-component contracts (event schema, control-plane ↔ runner API, AHP/ACP boundaries) before implementation starts.
- **Infrastructure What-If** — owned by Basher; every infra change is reviewed against a `what-if` diff before it is applied.
- **Security review** — owned by Linus; required for every security-sensitive change platform-wide, regardless of implementer.
- **Integration demonstration** — owned by Livingston; demonstrates the verified capability end-to-end with evidence before sign-off.
- **Sprint-close** — chaired by Danny; all agents participate; Scribe records outcomes, decisions, and evidence trails for the cycle. Completion remains subject to independent verification and review.

## Delivery sequencing

Danny manages program milestones and priorities within approved scope and Steve's technical sequencing. The shared delivery order remains:

1. Landing zone and trusted builds
2. One steerable, governed squad
3. Additional clients plus memory/MCP adapters
4. Multi-project scale and operations

This hiring/configuration pass establishes the roster and governance only; implementation proceeds in the sequence above under future instructions.
