# Copilot instructions for pull request tasks

When working on pull-request tasks in this repository:

- Default to implementing requested changes directly in code instead of returning only analysis or a plan.
- Make the smallest complete change set that fully satisfies the request.
- If requirements are ambiguous, ask one concise clarifying question; otherwise proceed with implementation.
- Update related tests/docs when the change affects behavior or developer workflow.
- Run relevant validation commands before and after edits when possible.
- If environment limitations prevent full validation, state exactly what could not be run and why.
- Keep changes scoped to the request; avoid unrelated refactors.
- Provide a short final summary with files changed, validation results, and any remaining manual steps.
