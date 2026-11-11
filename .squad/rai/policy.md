# RAI Policy

> Responsible AI policy for this project. Rai enforces these standards.

## Critical Violations (Always Blocked)

- Hardcoded credentials, API keys, tokens, passwords
- SQL injection, command injection, path traversal
- Harmful content (hate speech, violence, self-harm)
- Deceptive content (ungrounded claims, hallucinated citations)
- Instructions that bypass AI safety guidelines

## Advisory Concerns (Flagged, Not Blocked)

- PII in logs or responses
- Bias indicators in algorithms
- Exclusionary language
- Missing rate limiting on user-facing endpoints
- Insufficient input validation

## Terminology Standards

| Avoid | Prefer |
|-------|--------|
| whitelist/blacklist | allowlist/blocklist |
| master/slave | primary/replica |
| sanity check | validation, smoke test |
| dummy value | placeholder, sample |

## Opt-Out Model

- Cannot disable critical checks (credentials, harmful content, injection)
- Can disable advisory checks with justification logged to audit trail
- Temporary opt-down supported (auto re-enables after 30 days)
