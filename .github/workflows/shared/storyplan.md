---
# StoryPlan for GitHub Agentic Workflows. No containers, no package feed.
#
# Builds StoryPlan from source on the runner, serves it as an HTTP MCP server, and adds a publish-storyplan
# safe output that files the finished pre-PR as a GitHub issue under the story.
#
# Use from any repository:
#   imports:
#     - uses: garrettlondon1/storyplan/.github/workflows/shared/storyplan.md@main
#       with:
#         ref: main            # StoryPlan version (tag, branch or SHA) to build
#
# Trust boundaries
#   - StoryPlan runs on the runner, outside the agent sandbox, the same way gh-aw's own mcp-scripts server
#     does. It reads the checkout through git and writes only under /tmp/gh-aw/agent/storyplan.
#   - gh-aw rewrites its localhost URL to host.docker.internal and routes the agent through the MCP gateway;
#     the agent's firewall allows nothing else, and runner ports aren't reachable from outside.
#   - The issue is created by the publish-storyplan job, not the agent. It reads the body StoryPlan rendered
#     from the plan, sanitizes it with gh-aw's sanitizer, and checks the embedded plan matches the id.
#   - Acceptance criteria come from the story text registered before the agent starts, so the agent can't
#     drop or reword them.

import-schema:
  ref:
    type: string
    required: false
    default: main
    description: Git ref of garrettlondon1/storyplan to build.

mcp-servers:
  storyplan:
    type: http
    url: http://localhost:8766/mcp
    allowed:
      - repo_map
      - find_symbol
      - get_symbol
      - standards
      - plan_start
      - plan_add
      - plan_remove
      - plan_update
      - plan_check
      - plan_show
      - plan_publish
      - plan_list

steps:
  - uses: actions/setup-dotnet@v6.0.0
    with:
      dotnet-version: "10.0.x"
  - name: Build StoryPlan
    env:
      STORYPLAN_REF: ${{ github.aw.import-inputs.ref }}
    run: |
      set -euo pipefail
      src="${RUNNER_TEMP}/storyplan-src"
      git clone --quiet --filter=blob:none https://github.com/garrettlondon1/storyplan.git "${src}"
      git -C "${src}" checkout --quiet "${STORYPLAN_REF}"
      dotnet publish "${src}/src/StoryPlan.Mcp/StoryPlan.Mcp.fsproj" -c Release -o "${RUNNER_TEMP}/storyplan" --nologo -v q
      echo "StoryPlan $(git -C "${src}" rev-parse --short HEAD) built"

pre-agent-steps:
  # Register the story exactly as the person wrote it, before the agent sees anything, then start the
  # server. plan_start prefers the registered copy, which keeps acceptance criteria out of the agent's hands.
  - name: Start StoryPlan
    env:
      STORY_ID: ${{ github.event.issue.number }}
      STORY_TITLE: ${{ github.event.issue.title }}
      STORY_BODY: ${{ github.event.issue.body }}
    run: |
      set -euo pipefail
      home=/tmp/gh-aw/agent/storyplan
      mkdir -p "${home}/stories" /tmp/gh-aw/mcp-logs/storyplan
      if [ -n "${STORY_ID}" ]; then
        printf '%s\n\n%s\n' "${STORY_TITLE}" "${STORY_BODY}" > "${home}/stories/STORY-${STORY_ID}.md"
        echo "registered STORY-${STORY_ID}"
      fi
      # Same binding as gh-aw's mcp-scripts server: all interfaces, reached via host.docker.internal.
      nohup "${RUNNER_TEMP}/storyplan/storyplan" mcp --http --host 0.0.0.0 --port 8766 \
        --repo "${GITHUB_WORKSPACE}" --home "${home}" > /tmp/gh-aw/mcp-logs/storyplan/server.log 2>&1 &
      for i in $(seq 1 60); do
        if curl -fsS "http://127.0.0.1:8766/healthz" > /dev/null; then echo "StoryPlan up on :8766"; exit 0; fi
        sleep 1
      done
      cat /tmp/gh-aw/mcp-logs/storyplan/server.log
      exit 1

post-steps:
  # Hand the published pre-PR to the publish job. (Safe-job `artifacts:` does this natively on gh-aw
  # newer than v0.88.8; this keeps the import working on v0.88.8.)
  - name: Upload StoryPlan pre-PR
    if: always()
    uses: actions/upload-artifact@v7.0.1
    with:
      name: storyplan
      path: /tmp/gh-aw/agent/storyplan/issues/
      if-no-files-found: ignore
      retention-days: 7

safe-outputs:
  jobs:
    publish-storyplan:
      description: >-
        File the published StoryPlan pre-PR as a GitHub issue (or update the existing one) and link it
        under the story issue. Call once, after plan_publish with target 'issue' succeeded.
      runs-on: ubuntu-latest
      output: "Pre-PR issue filed."
      permissions:
        contents: read
        issues: write
      inputs:
        plan_id:
          description: "The plan id passed to plan_publish, e.g. STORY-12"
          required: true
          type: string
      steps:
        - name: Fetch publisher
          env:
            STORYPLAN_REF: ${{ github.aw.import-inputs.ref }}
          run: |
            set -euo pipefail
            git clone --quiet --filter=blob:none https://github.com/garrettlondon1/storyplan.git "${RUNNER_TEMP}/storyplan-src"
            git -C "${RUNNER_TEMP}/storyplan-src" checkout --quiet "${STORYPLAN_REF}"
        - name: Download StoryPlan pre-PR
          uses: actions/download-artifact@v8.0.1
          with:
            name: storyplan
            path: ${{ runner.temp }}/storyplan-issues
        - uses: github/gh-aw/actions/setup@v0.88.8
        - name: File pre-PR issue
          uses: actions/github-script@v9.0.0
          env:
            STORY_ISSUE: ${{ github.event.issue.number }}
            ISSUES_DIR: ${{ runner.temp }}/storyplan-issues
          with:
            script: |
              const fs = require("node:fs");
              const { publish } = require(`${process.env.RUNNER_TEMP}/storyplan-src/.github/storyplan/publish-storyplan.cjs`);
              const { sanitizeContent } = require(`${process.env.RUNNER_TEMP}/gh-aw/actions/sanitize_content.cjs`);
              const output = JSON.parse(fs.readFileSync(process.env.GH_AW_AGENT_OUTPUT, "utf8"));
              await publish({
                github, core, repo: context.repo,
                items: output.items.filter(i => i.type === "publish_storyplan"),
                issuesDir: process.env.ISSUES_DIR,
                sanitize: (s) => sanitizeContent(s),
                storyIssue: process.env.STORY_ISSUE ? Number(process.env.STORY_ISSUE) : null,
                staged: process.env.GH_AW_SAFE_OUTPUTS_STAGED === "true",
              });
---

## StoryPlan

StoryPlan tools are available as `storyplan`. Build plans through them: never guess symbol handles, never
write acceptance criteria yourself, and keep every description to one plain-English sentence.
