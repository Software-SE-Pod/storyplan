// Files a StoryPlan pre-PR as a GitHub issue. Runs in the publish-storyplan safe-output job, after the agent,
// with issues:write. It never trusts the agent's text: it reads the body StoryPlan rendered from the plan,
// sanitizes it with gh-aw's sanitizer, checks the embedded plan matches the requested id, and links the
// story issue as the parent.
//
// Exported as a function so tests can drive it with a fake Octokit.

const fs = require("node:fs");
const path = require("node:path");

const TAG = "storyplan-v1";
// The trigger label (`storyplan`) is removed by gh-aw after activation; pre-PR issues carry their own label.
const LABEL = "pre-pr";
const MAX_BODY = 65000;

/** The plan JSON embedded in an issue body (same rule as StoryPlan's IssueBody.extract). */
function extractPlan(body) {
  const m = body.replace(/\r\n/g, "\n").match(new RegExp("^(`{3,})json " + TAG + "\\n([\\s\\S]*?)\\n\\1[ \\t]*$", "m"));
  if (!m) return null;
  try {
    return JSON.parse(m[2]);
  } catch {
    return null;
  }
}

/** Finds the existing pre-PR issue for a plan id so re-runs update instead of duplicating. */
async function findExisting(github, owner, repo, planId) {
  const title = `[pre-PR] ${planId}:`;
  const issues = await github.paginate(github.rest.issues.listForRepo, { owner, repo, labels: LABEL, state: "all", per_page: 100 });
  return issues.find(i => !i.pull_request && i.title.startsWith(title)) || null;
}

/**
 * @param {object} ctx
 * @param {any} ctx.github   Octokit (actions/github-script)
 * @param {any} ctx.core     @actions/core
 * @param {{owner:string, repo:string}} ctx.repo
 * @param {any[]} ctx.items  agent output items of type publish_storyplan
 * @param {string} ctx.issuesDir  where StoryPlan wrote <id>.md and <id>.title
 * @param {(s:string)=>string} ctx.sanitize  gh-aw sanitizeContent
 * @param {number|null} ctx.storyIssue  the issue that triggered the workflow (parent), if any
 * @param {boolean} ctx.staged  preview mode: log instead of writing
 */
async function publish({ github, core, repo, items, issuesDir, sanitize, storyIssue, staged }) {
  const { owner, repo: name } = repo;
  if (items.length !== 1) throw new Error(`expected exactly one publish_storyplan call, got ${items.length}`);
  const planId = String(items[0].plan_id || "");
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(planId)) throw new Error(`invalid plan_id '${planId}'`);

  const bodyPath = path.join(issuesDir, `${planId}.md`);
  const titlePath = path.join(issuesDir, `${planId}.title`);
  if (!fs.existsSync(bodyPath)) throw new Error(`no published plan for ${planId}; the agent must call plan_publish target=issue first`);

  // The plan block is data, not prose: lift it out before sanitizing (the sanitizer rewrites @mentions,
  // URLs and #refs, which would corrupt JSON), sanitize the readable part, then put the block back.
  const raw = fs.readFileSync(bodyPath, "utf8");
  const plan = extractPlan(raw);
  if (!plan || plan.id !== planId) throw new Error(`issue body for ${planId} has no matching embedded plan`);
  if (plan.status !== "published") throw new Error(`plan ${planId} is ${plan.status}; publish it first`);
  const split = raw.lastIndexOf("<details><summary>Plan data");
  const prose = split >= 0 ? raw.slice(0, split) : raw;
  const dataBlock = split >= 0 ? raw.slice(split) : "";
  const body = sanitize(prose).trimEnd() + "\n\n" + dataBlock;
  if (body.length > MAX_BODY) throw new Error(`issue body is ${body.length} chars; GitHub's limit is ${MAX_BODY}`);
  const title = (fs.existsSync(titlePath) ? fs.readFileSync(titlePath, "utf8") : `[pre-PR] ${planId}`).trim().slice(0, 256);
  const labels = [LABEL];

  if (staged) {
    core.info(`[staged] would file "${title}" (${body.length} chars) with labels ${labels.join(", ")}`);
    await core.summary.addHeading(title, 3).addRaw(body).write();
    return { staged: true };
  }

  const existing = await findExisting(github, owner, name, planId);
  let issue;
  if (existing) {
    issue = (await github.rest.issues.update({ owner, repo: name, issue_number: existing.number, title, body, state: "open" })).data;
    await github.rest.issues.addLabels({ owner, repo: name, issue_number: issue.number, labels });
    core.info(`updated ${issue.html_url}`);
  } else {
    issue = (await github.rest.issues.create({ owner, repo: name, title, body, labels })).data;
    core.info(`created ${issue.html_url}`);
  }

  if (storyIssue && storyIssue !== issue.number) {
    try {
      await github.request("POST /repos/{owner}/{repo}/issues/{issue_number}/sub_issues", {
        owner, repo: name, issue_number: storyIssue, sub_issue_id: issue.id, replace_parent: true,
      });
    } catch (e) {
      // Already linked, or sub-issues unavailable: the cross-reference comment below still connects them.
      core.info(`sub-issue link skipped: ${e.message}`);
    }
    await github.rest.issues.createComment({
      owner, repo: name, issue_number: storyIssue,
      body: `📐 Pre-PR ${existing ? "updated" : "ready"} for review: #${issue.number}. ${plan.changes.length} planned changes, ${plan.criteria.length} acceptance criteria covered.`,
    });
  }
  core.setOutput("issue_number", String(issue.number));
  core.setOutput("issue_url", issue.html_url);
  return { staged: false, issue, updated: !!existing };
}

module.exports = { publish, extractPlan };
