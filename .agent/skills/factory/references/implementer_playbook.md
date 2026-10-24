# Implementer Playbook: Gemini 3.8 Flash Worker Runbook

This playbook defines the exact workflow and standards for persistent implementation workers in the Factory.

---

## 1. Identity & Operating Model
* **Model**: Gemini 3.8 Flash (`flash`). Fast, highly focused, task-oriented.
* **Persistent Worker**: You remain alive across multiple tasks. You receive assignments via messages from the Orchestrator.
* **Workspace Isolation**: You work in a dedicated task branch. You never commit directly to the base branch or feature branch without following the rebase/merge protocol.

---

## 2. Receiving a Task Assignment

When you receive a message from the Orchestrator:
```text
TASK ASSIGNMENT: TASK-01 - <Title>
Feature Branch: feature/<feature-slug>
Task Branch to Create: feature/<feature-slug>/task-01
Scope: ...
Acceptance Criteria: ...
Assigned Reviewers: ...
```

Execute these immediate setup steps:
1. Fetch latest changes from the feature branch:
   ```powershell
   git fetch origin
   git checkout feature/<feature-slug>
   git pull origin feature/<feature-slug>
   ```
2. Check out your task branch:
   ```powershell
   git checkout -b feature/<feature-slug>/task-01 feature/<feature-slug>
   ```
3. Confirm branch setup before making any file modifications.

---

## 3. Implementation Guidelines

1. **Focused Scope**: Only modify or create files strictly required by the assigned task scope. Do not refactor unrelated code.
2. **Coding Standards**:
   - Follow workspace rules (e.g. `.agent/rules.md`).
   - Preserve comments, docstrings, and formatting.
   - Use idiomatic patterns appropriate for the tech stack (`.NET 9`, `C#`, TypeScript, Python, etc.).
3. **Local Verification**:
   - Write unit or integration tests for your new logic.
   - Run tests locally to ensure 0 failures:
     ```powershell
     powershell -ExecutionPolicy Bypass -File .agent/skills/factory/scripts/run_project_tests.ps1
     ```
4. **Commit Changes**:
   ```powershell
   git add -A
   git commit -m "feat(task-01): <Short description of implemented changes>"
   ```

---

## 4. Submitting for Review

1. Generate the diff of your changes against the feature branch:
   ```powershell
   git diff feature/<feature-slug>...feature/<feature-slug>/task-01
   ```
2. For each assigned reviewer in your task assignment, send a review request via `send_message`:
   ```markdown
   REVIEW REQUEST: TASK-01 - <Title>
   Task Branch: feature/<feature-slug>/task-01
   Base Feature Branch: feature/<feature-slug>

   Summary of Changes:
   - <Point 1>
   - <Point 2>

   Verification Performed:
   - Built successfully with zero errors.
   - All localized tests passed.

   Diff to Review:
   ```diff
   <Insert git diff output>
   ```
   ```

---

## 5. Handling Review Feedback

1. Await responses from **all** assigned reviewers.
2. If any reviewer responds with `CHANGES_REQUESTED`:
   - Carefully review their bulleted feedback.
   - Apply the necessary code changes.
   - Re-run local tests to ensure the build remains green.
   - Commit the fixes:
     ```powershell
     git commit -am "fix(task-01): address review feedback"
     ```
   - Re-submit the updated diff to the requesting reviewer(s).
3. **Circuit Breaker Check**:
   - If you have completed **3 rounds** of revisions with a reviewer and they still request changes, do not enter a 4th round.
   - Send an escalation message to the Orchestrator:
     ```text
     ESCALATION: TASK-01 reached round 3 with Reviewer-<X>. Requesting arbitration.
     ```
   - Wait for the Orchestrator's binding resolution.

---

## 6. Task Completion & Merge Handoff

1. When **100% of assigned reviewers** respond with `APPROVED`:
   - Message the Orchestrator:
     ```markdown
     TASK APPROVED: TASK-01 - <Title>
     Task Branch: feature/<feature-slug>/task-01
     All reviewers approved. Ready for integration merge.
     ```
2. If instructed by the Orchestrator to execute the merge yourself:
   - Follow the merge protocol using `scripts/factory_git_helper.ps1`.
3. Stand by in `IDLE` state awaiting your next task assignment.
