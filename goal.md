# Vision and Goals

## Mission

Make AI-assisted engineering a team capability that people can steer, trust and improve, rather than a process tied to one developer's machine.

## Vision

A project team can delegate engineering work without giving up control. An operator can see what is happening, change direction and stop unsafe work. A developer can inspect the workspace and review the resulting changes. A platform team can support multiple projects without sharing their credentials or state. Reviewers can reconstruct what happened and decide whether the results deserve trust.

## Problem statement

The source assessment describes coding teams running on developer machines with local credentials, persistent repository state and broad shell access. That setup works for individual use but makes shared operation, isolation, consistent policy and recovery difficult.

Moving an agent into a hosted service does not automatically govern the commands it executes. Teams need controls at the point of execution, usable human intervention and evidence spanning the whole run. Passing software tests alone also does not establish that an agent follows instructions or produces useful work.

## Target audience

- **Platform teams** providing a repeatable, governed engineering environment.
- **Project operators and technical leads** delegating work and intervening when priorities change.
- **Developers and reviewers** inspecting workspaces, validating changes and controlling merges.
- **Security and assurance teams** defining policy and investigating decisions.
- **Product and delivery teams** connecting backlog priorities with visible outcomes.

## Business objectives

| ID | Objective | Desired outcome |
|---|---|---|
| GO-01 | Make delegation controllable | People can observe, redirect, approve and stop work without taking over the hosting environment. |
| GO-02 | Contain risk by project | A run cannot borrow another project's credentials, network permissions or writable state. |
| GO-03 | Make outcomes accountable | Changes, decisions, policy enforcement and costs can be traced to a run. |
| GO-04 | Make teams portable and repeatable | Reviewed team knowledge and configuration survive disposable execution environments. |
| GO-05 | Improve quality through evidence | Reproducible evaluations and independent review reveal regressions before release. |
| GO-06 | Scale without losing oversight | Onboarding, resource limits and operating procedures remain understandable across projects. |

## Value proposition and principles

- **People retain authority.** Delegation should reduce routine work, not remove accountability for priorities, approvals or merges.
- **Containment before convenience.** Projects should be isolated even when agents execute arbitrary build tools, because prompt instructions are not a security boundary.
- **Evidence before confidence.** A visible answer is not proof of correct execution; decisions and results must be independently verifiable.
- **Durable knowledge, disposable compute.** Teams should retain reviewed learning without depending on the lifetime of a machine.
- **Open interfaces, bounded promises.** Integrations should be replaceable, and experimental capabilities should not become hidden prerequisites.

Together these principles offer a shared engineering capability with clearer control and evidence than an unmanaged local agent session. They are intended benefits, not claims of measured productivity or financial return.

## Success criteria

The pilot will test the following proposed targets. They are not achieved results or contractual service levels; measurement definitions belong in the [product requirements](./prd.md).

| Outcome | Proposed evidence |
|---|---|
| Timely intervention | Steering acknowledgement p95 under 3 seconds. |
| Visible execution | A run log appears in both the console and configured telemetry destination at p95 under 10 seconds. |
| Repeatable startup | Start from a prepared team snapshot at p95 under 60 seconds. |
| Accountable completion | 100% of tested completed runs have complete audit manifests and verifiable hash chains. |
| Contained execution | Zero long-lived credentials in run images, files or environment; zero policy-bypassing tool calls in the agreed adversarial suite. |
| Evidence-based quality | Behaviour-changing releases meet agreed evaluation thresholds and independent review gates. Numerical quality thresholds remain to be established from a baseline. |
| Controlled expansion | Factory-readiness demonstration covers at least five projects and 50 concurrent runs. |

## Scope and non-goals

The product covers governed engineering runs, human steering, project isolation, team lifecycle, approved context/tools, evidence, evaluation and operational visibility.

It does not replace the existing team orchestration framework, remove human merge accountability, support arbitrary model providers, or promise compatibility with every editor and enterprise knowledge source. Agent execution in a separate managed agent-hosting service, GPU workloads and sovereign-cloud support are outside the initial scope.

These are deliberate boundaries, not permanent restrictions; changing them requires a clear beneficiary, rationale and impact assessment.

## How we work and maintain alignment

Prefer small complete changes, explicit failures, independently checked results and reviewed knowledge promotion. Keep sensitive content out of routine logs and public documentation.

Product leadership and the platform team should validate the pilot outcomes with project operators, then review this vision at each milestone and when intended beneficiaries or outcomes change. The [PRD](./prd.md) defines behaviour; the [technical design](./tdd.md) defines implementation. Technical changes do not change this mission automatically.
