# Initialize the engineering squad

Initialize this repository's Squad team. **Hire and configure the squad only; do not implement the application or deploy infrastructure.**

We are building a platform that runs GitHub Copilot CLI Squad teams in Azure Container Apps Sandboxes: one isolated sandbox per run and one sandbox group/network/identity boundary per project. A private control plane manages projects, teams, runs, approvals and lifecycle. The Ralph web console lets authorized users converse with the squad, steer or queue messages, answer approvals, follow live logs and attach VS Code.

The in-sandbox runner owns the Copilot SDK session, serializes commands and publishes durable ordered events. AHP enables shared-session clients behind a feature flag; ACP is an optional edge adapter. Approved MCP servers and scoped memory are pluggable. Governance must fail closed, credentials stay out of run sandboxes through proxy injection, and correlated telemetry plus immutable audit evidence cover every run. Foundry supports scoped knowledge/governance capabilities, not agent execution.

Hire this cast:

- **Steve** - lead, architecture, coordination and contract ownership.
- **Basher** - Bicep for Foundry, VNet/private networking, identities/RBAC, ACR, ACA and supporting Azure resources; Python 3.13 build/deployment automation and remote ACR image builds.
- **Rusty** - agent harness, Copilot SDK session ownership, steering, AHP and optional ACP.
- **Reuben** - control plane, lifecycle, authorization, GitHub integration and audit writer.
- **Saul** - Ralph console, accessibility, VS Code attach and CLI experience.
- **Linus** - security, governance, policy enforcement and supply chain.
- **Livingston** - quality, agent evaluations, observability, reliability and acceptance verification; own evaluation datasets, scoring rubrics, regression baselines and release thresholds.
- **Tess** - memory service, scoped storage and MCP integrations.
- **Scribe** - decisions, histories, evidence and handoff records.
- **Ralph** - bounded backlog monitoring, readiness, claims and status.

Use the installed Squad version's supported initialization and configuration mechanisms. Inspect existing team files first; preserve customizations, do not force-import, and do not duplicate built-in Scribe or Ralph agents.

Create or update only the supported Squad configuration and coordinator integration needed to establish:

1. The team roster and member charters, with responsibilities, boundaries, collaborators and review duties.
2. Routing rules matching the ownership above: one accountable owner per issue; Steve handles ambiguous or cross-component work; Linus reviews security-sensitive changes; Livingston independently verifies quality and evaluations; Scribe records decisions; Ralph dispatches only dependency-ready, claimed work and never grants approvals.
3. Planning, contract review, infrastructure What-If, security review, integration demonstration and sprint-close ceremonies.
4. The ready -> claimed -> implemented -> verified -> reviewed -> done workflow, including isolated work, independent review and evidence-based completion.
5. Shared engineering constraints: .NET 10 services, React/TypeScript console, Bicep for persistent infrastructure, Python 3.13 for all automation, macOS-first development and protected local `.env` configuration.
6. Supply-chain rules: authentic publisher artifacts through approved feeds only; pin and lock direct, transitive, development and build dependencies, with verified upstream publication timestamps strictly earlier than selection time minus seven days. No direct public PyPI/NuGet access or fallback; missing feed configuration or age/provenance evidence blocks installation, not team configuration.
7. Runtime governance boundaries: no permission bypass or approve-all handlers; AHP feature-gated, ACP optional; credentials protected; Squad repository files remain authoritative.

Sequence future delivery as landing zone and trusted builds -> one steerable governed squad -> additional clients and memory/MCP adapters -> multi-project scale and operations. Record this sequence in team responsibilities; do not start implementation during hiring.

Respect current session model settings. Do not copy historical model pins, invent unsupported configuration fields, install dependencies, create cloud resources, start watch/autopilot loops or modify application code.

Verify that the generated configuration is consistent and recognized using available non-installing validation. If Squad tooling is unavailable, report the blocker and request the approved bootstrap path; do not claim initialization succeeded.

Finish with a concise roster, links to the files created or updated, validation results and any blockers. **Stop after initialization and wait for the next instruction.**