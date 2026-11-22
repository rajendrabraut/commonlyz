# QUESTIONS

### Q-001 [BLOCKING] — May the agent push `agent/files-converter` to GitHub?      Status: RESOLVED
- Context: T-007/T-009. CLAUDE.md allows pushes only when SPEC sets `AGENT_MAY_PUSH: true`, and there is no SPEC.md. Word conversions (LibreOffice) and the Files workflow can only be verified on GitHub Actions.
- Options: A) Push the branch (no merge) — reversible, triggers CI. B) Keep it local; owner pushes.
- Recommendation: A, because CI is the only place the LibreOffice path is tested.
- If no answer: the commit stays local on `agent/files-converter`.
- Owner answer: A — "yes push it to github" (chat, 2026-11-21). Push only; merging stays with the owner.

### Q-002 [NON-BLOCKING] — Confirm D-001 (MVC frontend) and D-002 (Rust/Python/LibreOffice engines).      Status: OPEN
- If no answer: keep as built.
- Owner answer:
