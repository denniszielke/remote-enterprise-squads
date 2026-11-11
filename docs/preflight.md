# Stage A offline preflight

Status: **implemented, not independently reviewed**. This bounded prerequisite
inventory supports FR-01 and NFR-08/09. It does not complete Stage A feasibility,
approve installation, activate a project, or prove deployment readiness.

## Run and configuration

Use an existing Python **3.13.x** installation on macOS:

```sh
python3.13 tools/preflight.py
python3.13 -m unittest discover -s tests -p test_preflight.py
```

No dependencies, restore, virtual-environment creation, local container daemon,
network/cloud calls, or installations are performed. The tool uses portable Python
APIs for Apple Silicon and Intel macOS; Intel users must supply existing compatible
binaries themselves. Architecture and cloud Linux behavior are not validated.
Use a project-local `.venv` for future approved dependency workflows, not a global
installation. Missing Python cannot produce the script's JSON; the shell fails
before execution. Other Python minor versions produce a blocked runtime check.

Configuration is **process environment only**. [`.env.example`](../.env.example)
contains empty project placeholders and CFS feed defaults for corporate laptops
and dev boxes only. Set/export the following names in your shell or approved
launcher; never put credential values in command arguments:

| Required name | Accepted syntax (not proof of authorization) |
|---|---|
| `RES_PROJECT_ID` | Lowercase letter followed by up to 62 lowercase letters, digits or hyphens |
| `AZURE_TENANT_ID` | UUID-shaped hexadecimal identifier |
| `AZURE_SUBSCRIPTION_ID` | UUID-shaped hexadecimal identifier |
| `AZURE_LOCATION` | Lowercase letter followed by 1–39 lowercase letters or digits |
| `RES_PYTHON_FEED_URL` | Single candidate approved Python feed HTTPS endpoint |
| `RES_NUGET_FEED_URL` | Single candidate approved NuGet feed HTTPS endpoint |
| `RES_NPM_FEED_URL` | Single candidate approved npm feed HTTPS endpoint |

No file is parsed, discovered or sourced by the CLI, including `.env` and
`.env.example`. There is no env-file option or env-file format. Existing process
values are the sole authority, so cannot be overwritten by a local file.
If you maintain protected local configuration, your separately approved launcher
must export it first; do not blindly source untrusted shell files. `.gitignore`
protects `.env`, `.env.*`, `*.env`, `*.env.*`, local `.venv` directories and
Python caches, while allowing `.env.example`. Gitignore does not protect
previously tracked files or secure files on disk.

Feed endpoints must have an ASCII DNS hostname, HTTPS, and no non-443 port,
userinfo, query, fragment, whitespace/control characters, backslashes or
comma/semicolon/pipe-separated alternatives. Credentials belong in a future
approved external authentication mechanism, never a URL. Known direct public
hosts and their subdomains are rejected case-insensitively, including trailing-dot
forms: `pypi.org`, `pythonhosted.org`, `pypi.python.org`, `nuget.org`, `npmjs.org`,
`npmjs.com`, and `yarnpkg.com`. The CLI does not implicitly select a default feed
or alternate registry, attempt fallback, test reachability/authentication, or
mutate package-manager configuration. Unknown hosts are only syntactically accepted: they are **not**
automatically approved. Redirects and mirrors are not contacted or validated.
Inherited pip/NuGet/npm configuration is not inspected or cleared; no restore is
authorized, and later installation tooling must enforce approved-source-only
behavior independently.

### CFS developer feed defaults

The template supplies these stable client-facing endpoints:

| Input | Corporate laptop/dev-box default |
|---|---|
| `RES_PYTHON_FEED_URL` | `https://packagefeedproxy.microsoft.io/pypi/simple/` |
| `RES_NUGET_FEED_URL` | `https://packagefeedproxy.microsoft.io/nuget/v3/index.json` |
| `RES_NPM_FEED_URL` | `https://packagefeedproxy.microsoft.io/npm/` |

These are explicit template values, not automatic runtime fallbacks. Missing or
empty process inputs still fail closed. No global pip, NuGet or npm settings are
changed, and no credentials are included.

**Do not copy the default proxy into CI, remote ACR builds, sandbox images or
automated package-manager bootstrap commands.** Those workloads require
separately approved, authenticated CFS-compliant Azure Artifacts feeds and
protocol-specific authentication. Do not configure clients with the
`ms-feed-*` replica browsing URLs; the stable proxy selects its backing replica.

CFS documents a seven-day service-side quarantine for public npm and PyPI
versions. That does not prove this repository's stricter requirement that every
selected artifact's verified original publication age is **greater than** seven
days. Feed selection does not verify publisher authenticity, integrity, the
locked full dependency graph, authentication or build-feed suitability. All
existing evidence gates remain blocked until separately verified.

Sources retrieved through WorkIQ on 2026-11-11:

- [npm seven-day quarantine](https://eng.ms/docs/coreai/devdiv/one-engineering-system-1es/1es-docs/secure-supply-chain/central-feed-services-cfs/npm-7-day-quarantine)
- [Finding packages in the default proxy: usage restrictions and replica browsing](https://eng.ms/docs/coreai/devdiv/one-engineering-system-1es/1es-docs/secure-supply-chain/central-feed-services-cfs/finding-packages-in-default-proxy.html)
- [NuGet local-machine enforcement: retrieved client endpoint source](https://eng.ms/cid/74713a0b-a893-4b3a-8bfd-cc8de3c93b86/fid/5e39d5f953a131964e8d2640087b8943588bd3e166833c93e7dccac0ef48f9ca)
- [PyPI local-machine enforcement: retrieved endpoint and quarantine source](https://eng.ms/cid/74713a0b-a893-4b3a-8bfd-cc8de3c93b86/fid/7d8c6e99fb7992445be7a9d474d3c251e762b6c391028f2e3c2617c0cdb42519)

## Checks and report

The CLI checks its own interpreter version and probes existing executables:
`az --version`, `bicep --version`, `dotnet --list-sdks`, `gh --version`,
`git --version`, `node --version`, and `npm --version`. Bicep must already be
available as a standalone executable; the CLI never invokes an installation
helper. Each probe resolves through the current executable search path, uses a
fixed argument array without a shell, disables stdin, discards stdout/stderr,
and has a five-second process timeout (seven sequential probes, approximately
35 seconds maximum subprocess wait, excluding OS process creation/termination).
A minimal child environment disables common telemetry/first-use prompts and
does not forward project configuration or credentials. Existing local tools and
PATH remain a trust prerequisite; this is not a sandbox against malicious tools,
nor a guarantee that vendor executables have no local side effects.

One compact, deterministically ordered JSON object is written to stdout:
`schema_version`, `implementation_status`, overall `status`, and `checks`.
Every check contains only a fixed `id`, `status` (`PASS` or `BLOCKED`), and
fixed `reason` code. Values, discovered paths, tool versions/output, raw input,
timestamps and exception messages are never included. Missing, invalid,
nonzero-exit, failed and timed-out probes are distinguished using safe reasons.
Unsupported arguments produce a redacted blocked JSON report without echoing
arguments; `--help` prints static usage.

`PASS` on configuration means syntax only; `PASS` on a tool means its local
probe exited zero, not that the required version is installed. In particular,
`dotnet --list-sdks` does not prove .NET 10 is installed. Tool compatibility and
pinning need separate evidence.

The following gates always remain **BLOCKED**, even with valid inputs/tools:

- Feed approval and authentication contract.
- Authentic publisher, provenance and artifact integrity.
- Verified original publication age strictly greater than seven days at selection.
- Locked full direct/transitive/development/build dependency graph.
- Pinned CLI/SDK/Squad/runtime compatibility.
- Azure region/quota/API, private-network, identity and role feasibility.

The command therefore exits **1** while these required evidence gates remain
unresolved; **0** is reserved for an entirely passing report (`--help` also
exits 0). No flag, environment value or unverified file can mark evidence gates
passed. This tool deliberately does not collect or validate that evidence yet.
Hand off the report for separate verification and independent review; do not
treat a successful unittest run as approval or real-cloud capability evidence.
