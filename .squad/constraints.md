# Squad Constraints

## Shared engineering constraints

- **.NET 10** for services
- **React/TypeScript** for the Ralph console
- **Bicep** for persistent infrastructure (Azure AI Foundry, VNet/private networking, identities/RBAC, ACR, ACA)
- **Python 3.13** for all automation (build, deployment, remote ACR image builds)
- **macOS-first** development
- Local configuration lives in a protected `.env` file that is **never committed**

## Supply-chain rules

- Only authentic publisher artifacts through **approved feeds** are permitted — no unofficial mirrors or unverified sources.
- **Pin and lock** every dependency tier: direct, transitive, development, and build dependencies.
- Every dependency's upstream publication timestamp must be **verified** and **strictly earlier than selection time minus seven (7) days**.
- **No direct public PyPI/NuGet access or fallback** — approved feeds only, with no bypass path.
- **Missing feed configuration or missing age/provenance evidence blocks installation outright.** This is an installation-time hard stop, not a team-configuration concern, and it never silently proceeds.
- Linus owns and enforces these rules; any new dependency or MCP integration is reviewed by Linus before it is approved.

## Runtime governance boundaries

- **No permission-bypass or approve-all handlers**, anywhere in the platform — ever.
- **AHP is feature-gated** — shared-session clients are never enabled by default.
- **ACP is optional** — the edge adapter is never a required path.
- **Credentials are protected via proxy injection** and must never be present inside run sandboxes.
- **Squad repository files (`.squad/`) remain authoritative** over any other configuration source — no external tool, prompt, or ad-hoc convention overrides what is recorded here.
- Governance **fails closed**: any ambiguity, missing evidence, or unverifiable claim blocks progress rather than defaulting to allow.
