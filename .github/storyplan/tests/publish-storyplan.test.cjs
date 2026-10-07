// Drives publish-storyplan.cjs the way the gh-aw safe-output job does, with gh-aw's real sanitizer and a fake
// Octokit that records calls. Usage: node publish-storyplan.test.cjs <issuesDir> <ghAwSetupJsDir>
// The fixture (fixtures/STORY-7.*) is real StoryPlan output for a small sample repository.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");

const [fixtures, setupJs] = process.argv.slice(2);
global.core = { info: () => {}, warning: () => {}, debug: () => {} };
const { sanitizeContent } = require(path.join(setupJs, "sanitize_content.cjs"));
const { publish, extractPlan } = require(path.join(__dirname, "..", "publish-storyplan.cjs"));

// Work on a copy so the committed fixture is never touched.
const issuesDir = fs.mkdtempSync(path.join(os.tmpdir(), "storyplan-issues-"));
for (const f of fs.readdirSync(fixtures)) fs.copyFileSync(path.join(fixtures, f), path.join(issuesDir, f));
const ID = "STORY-7";

function fakeGitHub() {
  const issues = [];
  const calls = [];
  let next = 8;
  return {
    calls,
    paginate: async (fn, params) => (await fn(params)).data,
    request: async (route, params) => { calls.push(["request", route, params]); return { data: {} }; },
    rest: {
      issues: {
        listForRepo: async ({ labels }) => ({ data: issues.filter(i => i.labels.includes(labels)) }),
        create: async (p) => {
          calls.push(["create", p]);
          const i = { ...p, number: next, id: 9000 + next, html_url: `https://github.com/o/r/issues/${next}` };
          next++;
          issues.push(i);
          return { data: i };
        },
        update: async (p) => {
          calls.push(["update", p]);
          const i = issues.find(x => x.number === p.issue_number);
          Object.assign(i, p);
          return { data: i };
        },
        addLabels: async (p) => { calls.push(["addLabels", p]); return { data: [] }; },
        createComment: async (p) => { calls.push(["comment", p]); return { data: {} }; },
      },
    },
  };
}

const outputs = {};
const core = { ...global.core, setOutput: (k, v) => (outputs[k] = v), summary: { addHeading() { return this; }, addRaw() { return this; }, write: async () => {} } };

(async () => {
  const github = fakeGitHub();
  const base = { github, core, repo: { owner: "o", repo: "r" }, issuesDir, sanitize: s => sanitizeContent(s), storyIssue: 7, staged: false };
  const items = [{ type: "publish_storyplan", plan_id: ID }];
  const raw = fs.readFileSync(path.join(issuesDir, `${ID}.md`), "utf8");

  // 1. First run creates the issue, links it under the story, and comments on the story.
  const first = await publish({ ...base, items });
  assert.equal(first.updated, false);
  const created = github.calls.find(c => c[0] === "create")[1];
  assert.equal(created.title, fs.readFileSync(path.join(issuesDir, `${ID}.title`), "utf8").trim());
  assert.deepEqual(created.labels, ["pre-pr"]);
  assert.equal(created.body.trimEnd(), raw.trimEnd(), "gh-aw's sanitizer leaves the rendered pre-PR unchanged");
  assert.match(created.body, /```mermaid\nflowchart LR/);
  assert.match(created.body, /✅ 2\/2 criteria have a test/);
  assert.match(created.body, /\+ test 'ignores an unknown code' +✓ #2/);
  const plan = extractPlan(created.body);
  assert.equal(plan.id, ID);
  assert.equal(plan.criteria.length, 2);
  const link = github.calls.find(c => c[0] === "request");
  assert.equal(link[1], "POST /repos/{owner}/{repo}/issues/{issue_number}/sub_issues");
  assert.equal(link[2].issue_number, 7);
  assert.equal(link[2].sub_issue_id, 9008);
  assert.match(github.calls.find(c => c[0] === "comment")[1].body, /Pre-PR ready for review: #8/);
  assert.equal(outputs.issue_number, "8");

  // 2. Re-running updates the same issue instead of creating a second one.
  const again = await publish({ ...base, items });
  assert.equal(again.updated, true);
  assert.equal(github.calls.filter(c => c[0] === "create").length, 1);
  assert.equal(github.calls.find(c => c[0] === "update")[1].issue_number, 8);
  assert.match(github.calls.filter(c => c[0] === "comment")[1][1].body, /Pre-PR updated/);

  // 3. The agent can't point the job at a plan it didn't publish, or at a path.
  await assert.rejects(publish({ ...base, items: [{ type: "publish_storyplan", plan_id: "STORY-999" }] }), /no published plan for STORY-999/);
  await assert.rejects(publish({ ...base, items: [{ type: "publish_storyplan", plan_id: "../../etc/passwd" }] }), /invalid plan_id/);
  await assert.rejects(publish({ ...base, items: [...items, ...items] }), /exactly one publish_storyplan/);

  // 4. A body whose embedded plan doesn't match the file name is refused.
  const forged = fs.mkdtempSync(path.join(os.tmpdir(), "storyplan-forged-"));
  fs.writeFileSync(path.join(forged, `${ID}.md`), raw.replace(`"id": "${ID}"`, '"id": "STORY-1"'));
  await assert.rejects(publish({ ...base, issuesDir: forged, items }), /no matching embedded plan/);

  // 5. Staged mode writes nothing.
  const before = github.calls.length;
  assert.equal((await publish({ ...base, items, staged: true })).staged, true);
  assert.equal(github.calls.length, before);

  console.log("publish-storyplan: 5 scenarios passed");
})().catch(e => { console.error(e); process.exit(1); });
