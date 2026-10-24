---
name: factory
description: >-
  Autonomous software development factory workflow for end-to-end feature delivery.
  Orchestrates a persistent pool of Gemini 3.8 Flash implementation and review subagents,
  isolated task branching, multi-stage peer reviews, sequential rebase-merging, full-branch audits,
  and a live progress dashboard. Activate when the user requests to build a feature using the factory,
  or runs /factory.
---

# Software Engineering Factory Skill

The **Factory Skill** automates the delivery of complex features through an autonomous, multi-agent software factory. It coordinates an **Orchestrator Agent**, a persistent pool of **$N$ Implementation Workers** (Gemini 3.8 Flash), and a persistent pool of **$M$ Domain Review Specialists** (Gemini 3.8 Flash).

---

## 1. Invocation & Parameter Parsing

When activated by a user request (e.g. `/factory "Feature description" --impl 4 --review 2`), extract or set the following configuration:

| Parameter | Flag | Default | Description |
| :--- | :--- | :--- | :--- |
| `feature_prompt` | Positional | Required | Detailed description of the feature or requirement to implement. |
| `impl_team_size` | `--impl` / `-i` | `4` | Number $N$ of persistent implementation worker subagents. |
| `review_team_size` | `--review` / `-r` | `2` | Number $M$ of persistent review specialist subagents. |
| `base_branch` | `--base` / `-b` | Current branch (or `main`/`master`) | The Git branch to base the feature on. |

If team sizes are not explicitly provided in the user prompt, use the defaults ($N=4, M=2$) without blocking.

---

## 2. Factory Execution Lifecycle

```
[User Request]
       │
       ▼
[Phase 1: Ingestion & Pool Initialization]
  ├── Create feature branch: feature/<feature-slug>
  ├── Initialize live dashboard: factory_dashboard.md
  ├── Spawn N Implementation Workers (Flash, branch workspace)
  └── Spawn M Review Specialists (Flash, review domains)
       │
       ▼
[Phase 2: Task Breakdown & Scheduling]
  ├── Orchestrator decomposes feature into atomic tasks
  └── Organizes tasks into parallel execution batches
       │
       ▼
[Phase 3: Worker Execution Loop (Persistent Pool)]
  ├── Send task to idle worker via send_message
  ├── Worker checks out feature/<slug>/task-<id> off latest feature branch
  ├── Worker writes code, runs local unit tests
  ├── Worker submits diff to all M review specialists
  └── 100% Approval required (Max 3 rounds before Orchestrator arbitrates)
       │
       ▼
[Phase 4: Sequential Rebase & Merge Integration]
  ├── Designated merge worker rebases & merges approved task branch into feature/<slug>
  ├── Runs test suite (scripts/run_project_tests.ps1)
  └── Worker returns to idle pool; next task batch dispatched
       │
       ▼
[Phase 5: Full-Branch Comprehensive Audit]
  ├── Review team audits full diff: git diff <base_branch>..feature/<slug>
  ├── If any review rejects: Create fix task -> implement -> review -> merge
  └── When 100% approved + green tests: Mark READY FOR USER TESTING
       │
       ▼
[Phase 6: Standby Mode & Manual Shutdown]
  ├── Keep worker pool alive while user manually tests
  ├── If user reports feedback/bugs: Assign immediate fix task to idle worker
  └── On user command "shutdown factory" or /factory stop: Gracefully kill pool & clean up
```

---

## 3. Step-by-Step Orchestrator Instructions

Follow the detailed playbooks located in `references/`:

1. **Scoping & Branching**:
   - Determine `<feature-slug>` (e.g. `feature/player-mute-toggle`).
   - Run Git helper:
     ```powershell
     powershell -ExecutionPolicy Bypass -File .agent/skills/factory/scripts/factory_git_helper.ps1 -Action CreateFeatureBranch -FeatureSlug "<feature-slug>"
     ```
2. **Dashboard Initialization**:
   - Create `factory_dashboard.md` in the conversation artifact directory (`<appDataDir>\brain\<conversation-id>\factory_dashboard.md`).
   - Follow the layout in [references/dashboard_template.md](references/dashboard_template.md).
3. **Spawn Persistent Subagent Pools**:
   - **$N$ Implementation Workers**:
     Use `invoke_subagent`:
     - `TypeName`: `factory-implementer` (or `self`)
     - `Role`: `Implementer-1`, `Implementer-2`, etc.
     - `Model`: `flash`
     - `Workspace`: `branch`
     - `Prompt`: Provide system instructions from [references/implementer_playbook.md](references/implementer_playbook.md).
   - **$M$ Review Specialists**:
     Map domains according to [references/reviewer_rubrics.md](references/reviewer_rubrics.md).
     Use `invoke_subagent`:
     - `TypeName`: `factory-reviewer` (or `self`)
     - `Role`: `Reviewer-1 (UI/UX)`, `Reviewer-2 (Backend)`, etc.
     - `Model`: `flash`
     - `Workspace`: `inherit`
     - `Prompt`: Provide domain rubrics from [references/reviewer_rubrics.md](references/reviewer_rubrics.md).
4. **Task Decomposition & Board Population**:
   - Break feature into atomic tasks with explicit inputs, touched files, and acceptance criteria.
   - Update `factory_dashboard.md`.
5. **Dispatch & Monitor**:
   - Message available idle workers via `send_message` with task payload.
   - Workers report completion and review approval.
   - In case of 3 consecutive rejection rounds, Orchestrator arbitrates per [references/orchestrator_playbook.md](references/orchestrator_playbook.md).
6. **Sequential Merge**:
   - When a batch of tasks is approved, have a designated merge agent rebase and merge each task into `feature/<feature-slug>` sequentially.
   - Run tests after each merge:
     ```powershell
     powershell -ExecutionPolicy Bypass -File .agent/skills/factory/scripts/run_project_tests.ps1
     ```
7. **Full-Branch Audit**:
   - After all tasks are merged, dispatch full diff review to all $M$ reviewers.
   - If changes requested: assign patch task to idle worker.
   - If approved: announce feature is ready for user testing.
8. **Standby & Shutdown**:
   - Keep workers running.
   - When the user asks to shutdown (e.g. `shutdown factory` or `/factory stop`):
     - Run [scripts/shutdown_factory.ps1](scripts/shutdown_factory.ps1).
     - Call `manage_subagents` with `Action: 'kill_all'`.
     - Archive dashboard.

---

## 4. Reference Documents & Helpers

- [references/orchestrator_playbook.md](references/orchestrator_playbook.md) — Comprehensive runbook for the lead Orchestrator agent.
- [references/implementer_playbook.md](references/implementer_playbook.md) — Guidelines for Flash worker subagents.
- [references/reviewer_rubrics.md](references/reviewer_rubrics.md) — Pass/fail review checklists for UI/UX, Backend, Security, and QA.
- [references/dashboard_template.md](references/dashboard_template.md) — Markdown dashboard layout and update instructions.
- [scripts/factory_git_helper.ps1](scripts/factory_git_helper.ps1) — Git branch, diff, and rebase automation script.
- [scripts/run_project_tests.ps1](scripts/run_project_tests.ps1) — Cross-stack test runner.
- [scripts/shutdown_factory.ps1](scripts/shutdown_factory.ps1) — Standby teardown and branch cleanup script.
