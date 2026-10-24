# Orchestrator Playbook: Factory Runbook

This runbook guides the primary Orchestrator Agent through managing the multi-agent software factory.

---

## 1. Principles of the Orchestrator
* **No Direct Product Code Editing**: The Orchestrator does not write feature code directly. All feature code is written by persistent Flash implementation workers.
* **Fire-and-Forget Autonomy**: Do not block the user with unnecessary confirmations once parameters are established. Progress autonomously from breakdown through implementation, reviews, integration, and final branch audit.
* **Persistent Pool Tracking**: Maintain an internal state map of worker statuses (`IDLE`, `WORKING`, `IN_REVIEW`, `MERGING`, `STANDBY`).
* **Deterministic Transparency**: Keep the `factory_dashboard.md` artifact updated on every state transition.

---

## 2. Inception & Subagent Spawning

### A. Parameter Extraction
Extract the following from the user's prompt or context:
- `Feature Description`: The exact specifications of what to build.
- `impl_team_size` ($N$): Defaults to `4`.
- `review_team_size` ($M$): Defaults to `2`.
- `feature_slug`: Generate a kebab-case branch name (e.g. `feature/smart-cache-manager`).

### B. Persistent Worker Pool Initialization
Use `invoke_subagent` to spawn the persistent pool:

#### 1. Implementation Workers (Size $N$)
```json
{
  "TypeName": "factory-implementer",
  "Role": "Implementer-1",
  "Model": "flash",
  "Workspace": "branch",
  "Prompt": "You are Implementer-1, a persistent implementation worker in the Factory. You receive tasks via messages, create a task branch off the latest feature branch, write robust code and tests, submit your diff to all review specialists, address feedback until 100% approved, and report completion. Read references/implementer_playbook.md."
}
```
Store each worker's `conversationId` and assign an identifier (`Worker-1`, `Worker-2`, ... `Worker-N`).

#### 2. Review Specialists (Size $M$)
Based on $M$, allocate domains using the bundling matrix in `reviewer_rubrics.md`:
```json
{
  "TypeName": "factory-reviewer",
  "Role": "Reviewer-1 (UI/UX & Frontend)",
  "Model": "flash",
  "Workspace": "inherit",
  "Prompt": "You are Reviewer-1 in the Factory. Your domains are UI/UX, Frontend Architecture, and Usability. You review code diffs submitted by workers against references/reviewer_rubrics.md and issue APPROVED or CHANGES_REQUESTED with actionable line items."
}
```
Store each reviewer's `conversationId` and domain.

---

## 3. Task Breakdown & Dependency Graph

1. Break the feature down into atomic units of work.
2. Structure each task with:
   - `Task ID`: e.g. `TASK-01`
   - `Title`: Short descriptive name
   - `Scope`: Files to create or modify, API contracts, data models
   - `Acceptance Criteria`: Clear verification steps
   - `Dependencies`: List of predecessor tasks that must be merged first
3. Group tasks into execution batches:
   - **Batch 1**: Independent foundations (e.g. backend models, DB migrations, core services).
   - **Batch 2**: Consumers & UI (e.g. views, controls, API integration).
   - **Batch 3**: End-to-end integration, glue code, telemetry.

---

## 4. Task Dispatch & Management

### A. Assigning Tasks
For each task ready in the queue:
1. Find an `IDLE` worker in the pool.
2. Send a task dispatch message using `send_message`:
   ```markdown
   TASK ASSIGNMENT: TASK-01 - <Title>
   Feature Branch: feature/<feature-slug>
   Task Branch to Create: feature/<feature-slug>/task-01
   Scope:
   <Details>
   Acceptance Criteria:
   <Criteria>
   Assigned Reviewers:
   - Reviewer-1 (UI/UX): <ConversationId_R1>
   - Reviewer-2 (Backend): <ConversationId_R2>
   ```
3. Update worker status to `WORKING` in `factory_dashboard.md`.

### B. Handling 3-Iteration Circuit Breaker
If an implementer receives `CHANGES_REQUESTED` from a reviewer 3 consecutive times:
1. The implementer alerts the Orchestrator.
2. The Orchestrator reviews the diff and the reviewer's feedback.
3. The Orchestrator issues a binding decision:
   - **Clarify/Modify Acceptance Criteria**: Provide unambiguous guidance to both parties.
   - **Split Task**: If the scope proved too large, split remaining items into a secondary task.
   - **Arbitrate Approval**: If the reviewer's demands exceed the task scope, override with a scoped sign-off.

---

## 5. Sequential Rebase & Merge Integration

When a worker completes a task and all $M$ reviewers approve:
1. Designate a merge worker (or the task worker) to integrate the code:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .agent/skills/factory/scripts/factory_git_helper.ps1 `
     -Action RebaseAndMerge `
     -FeatureSlug "feature/<feature-slug>" `
     -TaskBranch "feature/<feature-slug>/task-01"
   ```
2. Run automated test suites:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .agent/skills/factory/scripts/run_project_tests.ps1
   ```
3. If tests pass:
   - Mark task `MERGED` in `factory_dashboard.md`.
   - Set worker status back to `IDLE`.
   - Check dependency graph to unlock next tasks.
4. If merge conflicts or test failures occur:
   - Instruct the worker to resolve conflicts on their task branch, re-run tests, and re-submit.

---

## 6. Full-Branch Comprehensive Audit

Once all tasks in the feature plan are merged into `feature/<feature-slug>`:
1. Compute the cumulative diff against the base branch:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .agent/skills/factory/scripts/factory_git_helper.ps1 `
     -Action GetCumulativeDiff `
     -FeatureSlug "feature/<feature-slug>" `
     -BaseBranch "<base-branch>"
   ```
2. Dispatch full diff to all $M$ reviewers simultaneously via `send_message`.
3. Reviewer Evaluation:
   - Each reviewer evaluates the complete feature implementation against their domain rubric.
   - **Any Rejection**:
     - Orchestrator creates a new patch task (`TASK-PATCH-<n>`).
     - Assigns to an idle worker.
     - Follows standard implementation $\to$ review $\to$ merge cycle.
   - **100% Unanimous Approval**:
     - Run final clean build and full test suite.
     - Update dashboard to `READY FOR USER TESTING`.

---

## 7. Standby Mode & Manual Shutdown

1. **Entering Standby**:
   - Inform the user that the feature is ready for manual testing.
   - Keep the persistent worker pool alive.
   - If the user reports bugs or tweaks during testing, dispatch patch tasks immediately to idle workers.
2. **Executing Manual Shutdown**:
   - Triggered when the user says "shutdown factory", "stop factory", or runs `/factory stop`.
   - Run:
     ```powershell
     powershell -ExecutionPolicy Bypass -File .agent/skills/factory/scripts/shutdown_factory.ps1 `
       -FeatureSlug "feature/<feature-slug>"
     ```
   - Call `manage_subagents` with `Action: 'kill_all'`.
   - Update `factory_dashboard.md` status to `SHUTDOWN / ARCHIVED`.
   - Report final completion summary to the user.
