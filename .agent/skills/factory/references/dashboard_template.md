# Live Factory Dashboard Template

Use this template when creating and updating `factory_dashboard.md` in `<appDataDir>\brain\<conversation-id>\`.

```markdown
# 🏭 Factory Live Dashboard: <Feature Title>

**Feature Branch:** `feature/<feature-slug>`  
**Status:** `IN_PROGRESS` | `FULL_BRANCH_AUDIT` | `STANDBY_FOR_TESTING` | `SHUTDOWN`  
**Started:** `<Timestamp>`  
**Last Updated:** `<Timestamp>`  

---

## 👥 Persistent Team Allocation

### Implementation Workers ($N = <Count>$)
| Worker ID | Model | Status | Current Task | Active Branch |
| :--- | :--- | :--- | :--- | :--- |
| `Worker-1` | Gemini 3.8 Flash | `WORKING` | `TASK-01: Model setup` | `feature/<slug>/task-01` |
| `Worker-2` | Gemini 3.8 Flash | `WORKING` | `TASK-02: Backend API` | `feature/<slug>/task-02` |
| `Worker-3` | Gemini 3.8 Flash | `IDLE` | — | — |
| `Worker-4` | Gemini 3.8 Flash | `IDLE` | — | — |

### Review Specialists ($M = <Count>$)
| Reviewer ID | Model | Assigned Domains | Active Reviews |
| :--- | :--- | :--- | :--- |
| `Reviewer-1` | Gemini 3.8 Flash | Frontend, UI/UX | `TASK-01` |
| `Reviewer-2` | Gemini 3.8 Flash | Backend, Architecture, Security, QA | `TASK-01`, `TASK-02` |

---

## 📋 Task Execution Board

| Task ID | Title | Assigned Worker | Review Status | Integration | Status |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `TASK-01` | Core Data Model | `Worker-1` | `APPROVED` (2/2) | `MERGED` | ✅ Done |
| `TASK-02` | Endpoint Service | `Worker-2` | `IN_REVIEW` (1/2) | `PENDING` | 🔄 In Review |
| `TASK-03` | UI Component | `Worker-3` | `UNASSIGNED` | `PENDING` | ⏳ Queued |
| `TASK-04` | Integration Glue | `Worker-4` | `UNASSIGNED` | `PENDING` | ⏳ Blocked (Needs TASK-02) |

---

## 🔍 Full-Branch Review & Audit

| Reviewer | Domain | Audit Status | Feedback / Notes |
| :--- | :--- | :--- | :--- |
| `Reviewer-1` | UI/UX | `PENDING` | Awaiting all task merges |
| `Reviewer-2` | Backend & Security | `PENDING` | Awaiting all task merges |

---

## 📊 Build & Test Telemetry
- **Latest Build:** `PASS`
- **Automated Tests:** 24 passed, 0 failed
- **Circuit Breaker Triggers:** 0
```
