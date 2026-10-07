# StoryPlan

**A reviewable pre-PR for every user story, before any code is written.**

An agent turns the story into a typed plan of the exact files and symbols the change will add, alter or remove. Each one gets a single plain-English sentence, and each acceptance criterion is mapped to a test that will prove it. The plan is filed as a GitHub issue you can review like a PR. When the real PR arrives, CI checks it against the plan.

```
story issue ──label `storyplan`──▶ agentic workflow ──▶ pre-PR issue (sub-issue of the story)
                                        │                         │
                                StoryPlan MCP server        review it like a PR
                                (typed, gated plan)               │
                                                                  ▼
                         PR "Closes #story" ──▶ storyplan-verify ──▶ fails on missing or unplanned changes
```

## Why

Agents write code from a story nobody has turned into a technical plan, so the dev first sees the change as a finished diff. StoryPlan moves that review earlier and makes the plan something a machine can check:

- **The F# types are the contract.** `Plan`, `Change` and `NewSym` are F# unions and records. The JSON wire format and the JSON Schema in every MCP tool's `inputSchema` are generated from them, so they can't drift apart.
- **Every reference is checked.** Symbol handles look like `path#Type.member`. They have to exist at the planned commit, a new symbol's name has to match its own signature, and the server rejects a "change" that doesn't change anything.
- **Acceptance criteria come from the story, not the agent.** The server reads them out of the story text, registered before the agent runs. Every one must be covered by a test the plan adds.
- **Gates are enforced by the server, not the prompt.** A plan can't be published until it passes all of these:
  - every changed symbol was inspected;
  - every acceptance criterion has a test;
  - every coding standard that applies is cited.
- **It works across languages.** Symbols come from SCIP indexes (TypeScript, Python, Java, Go, Rust, C# and more) when present. Otherwise built-in C#, F# and TypeScript/JavaScript extractors are used.
- **Verify is deterministic.** No model is involved: the plan is compared with the PR's diff.

## Try it here

1. Open an issue describing a change to StoryPlan itself, with an `Acceptance criteria:` list.
2. Add the `storyplan` label, or comment `/storyplan`.
3. The workflow comments with a link to the pre-PR issue and links it as a sub-issue of the story.

See the issues labelled [`pre-pr`](../../issues?q=label%3Apre-pr) for real output.

## Use it in your repository

Add `.github/workflows/storyplan.md`:

```aw
---
on:
  label_command:
    name: storyplan
    events: [issues]
permissions:
  contents: read
  issues: read
  copilot-requests: write
engine: copilot
imports:
  - uses: Software-SE-Pod/storyplan/.github/workflows/shared/storyplan.md@main
    with:
      ref: main
tools:
  edit: false
  bash: []
safe-outputs:
  add-comment:
---
Plan story #${{ github.event.issue.number }} with StoryPlan (see this repository's storyplan.md for the full prompt).
```

Then run `gh aw compile`. No PAT or secret is needed: `copilot-requests: write` bills Copilot to the organization through the Actions token. The repository must be owned by an organization with a Copilot plan and the policy "Allow use of Copilot CLI billed to the organization" turned on. In a personal repository, drop that permission and set a `COPILOT_GITHUB_TOKEN` secret instead (a fine-grained PAT with Copilot Requests: Read).

Copy `storyplan-verify.yml` to get the PR gate.

Coding standards live in `.storyplan/standards.json`. Each rule has a glob and one of four deterministic checks: `forbid`, `precededBy`, `firstLine` or `captureMatches`. See [this repository's rules](.storyplan/standards.json).

### No containers, no package feed

The shared import builds StoryPlan from this repository at the `ref` you pin. It runs StoryPlan as a plain process on the runner, the same way gh-aw runs its own `mcp-scripts` server. gh-aw routes the agent to it through its MCP gateway and firewall. The agent can't edit code, and a separate job with `issues: write` files the issue. That job sanitizes the body with gh-aw's sanitizer and refuses a plan id that doesn't match what StoryPlan published.

## What the pre-PR issue contains

Everything is GitHub-native markdown:
- a summary alert;
- a Mermaid map from acceptance criteria to the tests that prove them, and to each file's symbols;
- an acceptance-criteria table;
- per-file change tables, with signatures linked to their lines at the planned commit;
- collapsible signature diffs;
- a pre-flight checklist;
- the plan JSON in a collapsed `json storyplan-v1` block that CI reads back.

## Local use

```bash
dotnet run --project src/StoryPlan.Mcp -- mcp --repo /path/to/repo             # MCP over stdio
dotnet run --project src/StoryPlan.Mcp -- mcp --http --port 8766 --repo .      # MCP over HTTP at /mcp
dotnet run --project src/StoryPlan.Mcp -- serve                                # plan UI at http://127.0.0.1:5199
dotnet run --project src/StoryPlan.Mcp -- show <id> --issue                    # the issue body
dotnet run --project src/StoryPlan.Mcp -- verify <issue.md|plan.json> main HEAD
```

| Tool | Purpose |
|---|---|
| `repo_map`, `find_symbol`, `get_symbol` | Signatures first, bodies on demand; `get_symbol plan=<id>` records the inspection |
| `standards` | The repository's rules |
| `plan_start`, `plan_add`, `plan_remove`, `plan_update` | Build the plan; each change is validated as it is added |
| `plan_check`, `plan_show` | The gates, and the plan rendered for review (an MCP Apps UI in hosts that support it) |
| `plan_publish` | Freeze it to the repository (`.stories/`) or as a GitHub issue |
| `plan_verify`, `plan_list` | Compare a PR with its plan; list plans |

## Limits

- The one-line descriptions are reviewed by people, not verified. Behaviour is proven by the tests that cover each criterion.
- Without a SCIP index, "Used in" counts are name-based estimates, shown with `~`.
- A change the agent never mentions can't be gated. Reviewers catch it on the pre-PR, and verify flags it as an unplanned edit.

## License

MIT
