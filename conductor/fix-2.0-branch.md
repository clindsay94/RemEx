# Fix 2.0 Branch Load Issue

## Objective
Fix the issue where the `2.0` branch cannot be loaded (checked out) on Windows due to the presence of files with invalid characters (`\r` - carriage return) in their names.

## Key Files & Context
- Problematic files in `origin/2.0`:
  - `"2\r"`
  - `"RemEx.Android/2\r"`
- Environment: Windows (where `\r` is an illegal character for filenames)

## Implementation Steps
1. **Create an isolated Git index:** To avoid modifying the current working directory, create a temporary Git index pointing to the `origin/2.0` branch.
2. **Remove invalid files:** Use `git rm --cached` within this isolated index to remove the files containing carriage returns.
3. **Commit the fix:** Write the modified index to a new tree and create a commit that uses `origin/2.0` as its parent.
4. **Create a new branch:** Update a new local branch reference (e.g., `2.0-local`) to point to this new commit.
5. **Checkout the branch:** Switch to the newly created `2.0-local` branch, which can now be safely checked out on Windows.

## Verification
- Run `git status` to ensure the checkout was successful.
- Run `git branch --show-current` to confirm we are on the new branch.