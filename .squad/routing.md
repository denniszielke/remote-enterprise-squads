# Squad Routing

## Work Type Rules

| Work Type | Primary Agent | Fallback |
|-----------|---------------|----------|

## Governance

- Route based on work type and agent expertise
- Update this file as team capabilities evolve

## Governance Rules

- One accountable owner per issue — routing below assigns a single primary agent per work type
- Danny owns program scope, prioritization, milestones and decision follow-through; Steve owns ambiguous technical or cross-component engineering work that has no single clear owner
- Danny can decide delivery trade-offs within approved scope, not architecture, security exceptions, spending, deployment authorization or release approval; material scope changes go to the human sponsor
- Linus is the required reviewer for every security-sensitive change platform-wide, regardless of who implemented it
- Livingston independently verifies quality and evaluations — reviewer is never the implementer; nothing reaches "verified" without Livingston's sign-off
- Scribe records all decisions, histories, evidence, and handoff records
- Ralph dispatches only dependency-ready, CLAIMED work from the backlog and never grants approvals itself

## Work Type → Agent

| Work Type | Primary | Secondary |
|-----------|---------|----------|
| Program direction, scope, priorities, milestones, decision coordination | Danny | Steve |
| Architecture, coordination, contracts | Steve | — |
| Bicep, Azure infra, ACR/ACA | Basher | — |
| Agent harness, Copilot SDK, steering | Rusty | — |
| Control plane, lifecycle, audit | Reuben | — |
| Ralph console, accessibility, CLI | Saul | — |
| Governance, policy, supply chain | Linus | — |
| Evaluations, observability, acceptance | Livingston | — |
| Memory service, scoped storage, MCP | Tess | — |
| Claim verification, hallucination detection, counter-hypothesis analysis, source validation | Fact Checker | — |
