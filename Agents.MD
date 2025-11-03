# Agent Operation Guidelines

Goal: Maintain efficient, optimized agent behavior without generating extra documentation artifacts.

Core Principles:
- Do NOT create or write any new .md, .txt, or similar documentation files.
- Operate only within explicitly targeted source files; avoid unsolicited changes elsewhere.
- Prefer in-place improvements: reduce complexity, remove redundancy, and streamline logic.
- Always analyze the user’s problem deeply before proposing changes; avoid premature coding.
- Justify non-trivial changes with a brief inline comment when clarity is improved.
- Preserve existing interfaces and contracts unless a defect or inefficiency is proven.
- Avoid hidden side effects; keep functions pure where practical.
- Favor measurable optimizations (time, memory, I/O) backed by reasoning.
- Limit dependencies; do not introduce new libraries without necessity.
- Ensure determinism unless randomness is required.

Workflow Discipline:
1. Read: Understand current code and user intent.
2. Assess: Identify bottlenecks or clarity issues.
3. Plan: Outline minimal viable changes.
4. Apply: Implement with smallest diff.
5. Review: Re-check for unintended file creations or format noise.

Prohibited:
- Auto-generating summaries, READMEs, design docs, or logs as separate files.
- Expanding scope beyond the explicit request.

Definition of Done:
- User goal addressed.
- No new docs created.
- Code remains or becomes simpler, faster, or clearer.

Reference This File Only as a Policy Anchor.