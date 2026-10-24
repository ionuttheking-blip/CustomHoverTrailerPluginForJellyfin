# Reviewer Rubrics & Dynamic Domain Allocation

This document defines the domain specializations, pass/fail rubrics, and the dynamic bundling matrix for Factory review specialists.

---

## 1. Dynamic Bundling Matrix

When the factory is spawned with review team size $M$, review domains are mapped dynamically:

| Review Team Size ($M$) | Reviewer | Assigned Specialization Domains |
| :---: | :--- | :--- |
| **$M = 1$** | **Reviewer-1** | Full-Stack: UI/UX, Backend Logic, Security & Resilience, QA & Tests. |
| **$M = 2$** | **Reviewer-1**<br/>**Reviewer-2** | **Frontend & UI/UX**: Presentation, CSS, components, accessibility.<br/>**Backend & Logic**: Domain logic, DB/API, security, test suites. |
| **$M = 3$** | **Reviewer-1**<br/>**Reviewer-2**<br/>**Reviewer-3** | **Frontend & UI/UX**: Layout, styling, UX interaction, DOM efficiency.<br/>**Backend & Architecture**: Data modeling, algorithms, system architecture.<br/>**Security & Quality**: Security, edge cases, error resilience, unit & integration tests. |
| **$M \ge 4$** | **Reviewer-1**<br/>**Reviewer-2**<br/>**Reviewer-3**<br/>**Reviewer-4** | **UI/UX Specialist**: Visuals, user flow, component cleanliness.<br/>**Backend Specialist**: Business logic, API contracts, database queries.<br/>**Security Specialist**: Input sanitization, auth, denial-of-service, vulnerability checks.<br/>**QA & Test Specialist**: Code coverage, regression prevention, mock integrity, edge cases. |

---

## 2. Domain Rubrics & Checklists

### Domain A: Frontend, UI & UX
* **Visual Presentation**: Does the code match UI design guidelines, responsive rules, and styling conventions?
* **Interaction**: Are user interactions (clicks, hovers, transitions, error states) smooth and non-blocking?
* **Accessibility**: Are proper ARIA attributes, labels, keyboard navigation, and focus states provided?
* **DOM & Performance**: No unnecessary re-renders, layout thrashing, or leaky event listeners.

### Domain B: Backend Architecture & Business Logic
* **Design & Modularity**: Are classes, functions, and interfaces single-responsibility and cleanly decoupled?
* **Correctness**: Does the logic satisfy all specified acceptance criteria and edge conditions?
* **Data Flow & Integrity**: Proper nullability checks, input validations, and error propagation.
* **Compatibility**: Strict adherence to framework versions (e.g. .NET 9.0, Jellyfin 10.11.3 APIs).

### Domain C: Security & Resilience
* **Input Validation & Sanitization**: All user/external inputs are validated and sanitized (no injection, path traversal).
* **Exception Handling**: Specific exception handling with informative logging; no swallowed exceptions.
* **Resource Management**: Streams, database connections, and file handles are properly disposed (`using` statements, `IDisposable`).
* **Concurrency**: Thread-safe operations; no race conditions or deadlocks.

### Domain D: QA, Code Quality & Test Verification
* **Test Coverage**: Are new logic paths, branch conditions, and boundary values covered with tests?
* **Test Quality**: Tests assert on meaningful behavior, not just trivial tautologies.
* **Clean Code**: No unused variables, commented-out dead code, or magic numbers.
* **Documentation**: Public APIs, complex algorithms, and configuration schemas have clear docstrings.

---

## 3. Review Response Format

Reviewers must strictly respond using one of the two formats:

### Option 1: Approval
```markdown
### REVIEW RESULT: APPROVED
Domain: <Assigned Domain(s)>
Summary:
- Changes thoroughly verified against the domain rubric.
- Code cleanly meets all acceptance criteria with no blocking defects.
```

### Option 2: Changes Requested
```markdown
### REVIEW RESULT: CHANGES_REQUESTED
Domain: <Assigned Domain(s)>

Required Modifications:
1. `path/to/file.cs#L45`: Null pointer risk when `payload.User` is null. Add guard clause or safe navigation.
2. `path/to/file.cs#L102`: Hardcoded timeout value. Extract to configuration constant.
3. Missing unit test covering the fallback scenario when external stream times out.
```
