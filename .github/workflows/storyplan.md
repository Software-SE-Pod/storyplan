---
name: storyplan
description: Turn a user story into a reviewable, typed pre-PR issue before any code is written.

on:
  # Label a story issue `storyplan` (or comment `/storyplan` on it) to plan it.
  label_command:
    name: storyplan
    events: [issues]
  slash_command:
    name: storyplan
    events: [issues, issue_comment]
  reaction: eyes

permissions:
  contents: read
  issues: read

engine:
  # Inference uses the COPILOT_GITHUB_TOKEN secret (a fine-grained PAT with Copilot Requests: Read), which
  # works in a user-owned public repository. In an organization with Copilot billing, use
  # `permissions: copilot-requests: write` instead and drop the secret.
  id: copilot

strict: true

imports:
  - uses: shared/storyplan.md
    with:
      ref: main

tools:
  github:
    toolsets: [issues]
  # Read-only: the agent plans through StoryPlan and never edits the repository.
  edit: false
  bash: []
  cli-proxy: false

network:
  allowed:
    - defaults

safe-outputs:
  add-comment:
    max: 1

timeout-minutes: 20
---

# StoryPlan: pre-PR for story #${{ github.event.issue.number }}

You turn a user story into a pre-PR: the exact files and symbols the change will touch, each explained in one
plain-English sentence, with tests that prove every acceptance criterion. Nobody writes code in this run.

The story is issue #${{ github.event.issue.number }} in this repository. The plan id is `STORY-${{ github.event.issue.number }}`.
The workflow has already registered the story text with StoryPlan under that id.

Follow these steps exactly. The StoryPlan server enforces each gate and tells you what to fix.

1. `plan_start` with id `STORY-${{ github.event.issue.number }}`, a short title, a one-sentence why, a loc estimate, and
   `story` set to the issue title and body. The server uses its registered copy and replies with the story's acceptance
   criteria, numbered. Every one needs a test you plan.
2. `repo_map` (budget 1500), then `find_symbol` for what the story touches. Never invent handles.
3. `get_symbol` with `plan` set to the id, on every symbol you will alter or remove.
4. `standards`, then `plan_update` with `standards` set to every rule whose glob matches a file you plan to change.
5. `plan_add` every change in one call, one change per symbol:
   - new member of an existing type: `extend`, named `Type.member`
   - changed member: `alter` that member, never its whole type; `newSig` only if the signature changes
   - new file: `newFile`; docs or config: `touch`
   - tests are new symbols named the way the index names them (`it('does x')` is `does_x`)
   - each test sets `covers` to the criterion numbers it proves; use the story's names for anything it names
   - `sig` is the exact first line of the declaration
   - `does` and `change` are one plain-English sentence a non-coder could follow, never code
   Fix every error it returns.
6. `plan_check` until it reports READY.
7. `plan_publish` with target `issue`, then call `publish_storyplan` with the same plan id.

If the story is too vague to plan (no clear behaviour, or it contradicts the code), don't guess. Skip steps 5 to 7
and use `add_comment` to ask one specific question on the story issue.
