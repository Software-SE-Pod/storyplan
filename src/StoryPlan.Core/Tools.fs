/// The tool surface, transport-agnostic. The MCP server and the HTTP UI host both dispatch here,
/// so an agent, the inline MCP App and the web view all hit the same validation.
module StoryPlan.Tools

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open StoryPlan.Model

type ToolResult =
    { Text: string
      /// Full payload for UIs (plan, check report, markdown). Null for model-only tools.
      View: JsonNode
      IsError: bool }

type Tool =
    { Name: string
      Description: string
      Args: (string * Type * bool * string) list
      /// MCP Apps visibility: "model", "app".
      Visibility: string list
      /// Render this tool's result with the plan UI.
      Ui: bool
      Run: JsonObject -> ToolResult }

let uiUri = "ui://storyplan/plan"

let private ok text = { Text = text; View = null; IsError = false }
let private err text = { Text = text; View = null; IsError = true }

exception ArgError of string

let private getProp (args: JsonObject) (name: string) : JsonNode =
    let mutable n: JsonNode = null
    if args.TryGetPropertyValue(name, &n) then n else null

let private arg<'T> (args: JsonObject) (name: string) : 'T =
    try Json.decode typeof<'T> name (getProp args name) :?> 'T
    with Json.DecodeError(p, m) -> raise (ArgError $"{p}: {m}")

let private opt<'T> (args: JsonObject) name : 'T option = arg<'T option> args name

let defaultRepo () =
    match Environment.GetEnvironmentVariable "STORYPLAN_REPO" with
    | null | "" -> Directory.GetCurrentDirectory()
    | r -> r

let private repoOf args =
    let r = opt<string> args "repo" |> Option.defaultValue (defaultRepo ())
    let r = Path.GetFullPath r
    if not (Git.isRepo r) then raise (ArgError $"repo: {r} is not a git repository")
    (Git.run r [ "rev-parse"; "--show-toplevel" ]).Trim() |> Path.GetFullPath

/// Build the default repo's index in the background so the first tool call doesn't pay for it.
let warmUp () =
    Threading.Tasks.Task.Run(fun () ->
        try
            let r = Path.GetFullPath(defaultRepo ())
            if Git.isRepo r then
                Engine.indexAt ((Git.run r [ "rev-parse"; "--show-toplevel" ]).Trim() |> Path.GetFullPath) "HEAD" |> ignore
        with _ -> ())
    |> ignore


/// The payload every plan UI renders.
let view (p: Plan) (report: Engine.CheckReport option) =
    let o = JsonObject()
    o["plan"] <- Json.toNode p
    report |> Option.iter (fun r -> o["check"] <- Json.toNode r)
    o["markdown"] <- JsonValue.Create(Engine.render p)
    let ix = Engine.indexAt p.Repo p.Sha
    let sigs = JsonObject()
    for c in p.Changes do
        match c with
        | Alter(h, _, _) | Remove(h, _) ->
            match ix.ByHandle.TryGetValue h with
            | true, s ->
                let info = JsonObject()
                info["sig"] <- JsonValue.Create s.Sig
                info["refs"] <- JsonValue.Create((Index.references ix s).Length)
                sigs[h] <- info
            | _ -> ()
        | _ -> ()
    o["symbols"] <- sigs
    o

let private summary (p: Plan) =
    $"{p.Id} [{p.Status}] {p.Changes.Length} changes, {p.Criteria.Length} criteria, {p.Standards.Length} standards @ {Engine.short p.Sha}"

let private findingsText (fs: Finding list) =
    String.Join("\n", fs |> List.map (fun f -> $"{(string f.Severity).ToLowerInvariant()} {f.Code}: {f.Message}"))

let private withPlan id (f: Plan -> ToolResult) =
    match Engine.Store.tryGet id with
    | Result.Ok p -> f p
    | Result.Error e -> err e

let private repoArg = "repo", typeof<string option>, false, "Repository root. Defaults to the server's working repo."

let all: Tool list =
    [ { Name = "repo_map"
        Description = "Signatures-only map of the repo, most-referenced files first, cut to a token budget. Lines are '<name> | <signature>'; handle = <path>#<name>. Start here."
        Args = [ repoArg
                 "under", typeof<string option>, false, "Path prefix to focus on, e.g. 'src/Orders/'"
                 "budget", typeof<int option>, false, "Max tokens (default 1500)" ]
        Visibility = [ "model" ]
        Ui = false
        Run = fun a ->
            let ix = Engine.indexAt (repoOf a) "HEAD"
            ok (Index.repoMap ix (opt a "under" |> Option.defaultValue "") (opt a "budget" |> Option.defaultValue 1500)) }

      { Name = "find_symbol"
        Description = "Find symbol handles by name, partial name or handle. Fuzzy. Use the returned handles verbatim in plans."
        Args = [ repoArg
                 "query", typeof<string>, true, "Name, Type.Member, or path#Name"
                 "limit", typeof<int option>, false, "Default 8" ]
        Visibility = [ "model" ]
        Ui = false
        Run = fun a ->
            let ix = Engine.indexAt (repoOf a) "HEAD"
            match Index.find ix (arg a "query") (opt a "limit" |> Option.defaultValue 8) with
            | [] -> ok "no matches"
            | hits -> ok (String.Join("\n", hits |> List.map (fun s -> $"{s.Handle} | {s.Sig}"))) }

      { Name = "get_symbol"
        Description = "Signature, body and referencing files for one handle. Pass plan to record the inspection: every altered or removed symbol must be inspected before publish."
        Args = [ repoArg
                 "handle", typeof<string>, true, "path#Name from find_symbol or repo_map"
                 "plan", typeof<string option>, false, "Plan id to record this inspection against"
                 "body", typeof<bool option>, false, "Include the body (default true, max 120 lines)" ]
        Visibility = [ "model" ]
        Ui = false
        Run = fun a ->
            let ix = Engine.indexAt (repoOf a) "HEAD"
            match Index.resolve ix (arg a "handle") with
            | Result.Error e -> err e
            | Result.Ok s ->
                let refs = Index.references ix s
                let full = Index.body ix s
                let endLine = s.Start + full.Split('\n').Length - 1
                let body =
                    if opt a "body" |> Option.defaultValue true then
                        let b = full.Split('\n')
                        "\n" + String.Join("\n", b |> Array.truncate 120) + (if b.Length > 120 then $"\n... {b.Length - 120} more lines" else "")
                    else ""
                let recorded =
                    match opt<string> a "plan" with
                    | Some id ->
                        match Engine.inspect id s.Handle with
                        | Result.Ok _ -> $"\n(recorded as inspected for {id})"
                        | Result.Error e -> $"\n(not recorded: {e})"
                    | None -> ""
                let shown = String.Join(", ", refs |> List.truncate 10)
                let more = if refs.Length > 10 then $" +{refs.Length - 10}" else ""
                ok $"{s.Handle} [{s.Kind}] lines {s.Start}-{endLine}\n{s.Sig}\nreferenced from {refs.Length} files: {shown}{more}{body}{recorded}" }

      { Name = "standards"
        Description = "The repo's coding standards: id, rule, glob, exemplar. Cite every rule whose glob matches a planned file via plan_update standards."
        Args = [ repoArg ]
        Visibility = [ "model"; "app" ]
        Ui = false
        Run = fun a ->
            match Standards.load (repoOf a) with
            | Result.Error e -> err e
            | Result.Ok [] -> ok "no standards file; see .storyplan/standards.json"
            | Result.Ok rules -> ok (String.Join("\n", rules |> List.map (fun r -> $"{r.Id} [{r.Glob}] {r.Text} (e.g. {r.Exemplar})"))) }

      { Name = "plan_start"
        Description = "Start (or restart, while draft) a plan for a story, pinned to the repo's HEAD commit."
        Args = [ repoArg
                 "id", typeof<string>, true, "Story id, e.g. STORY-12"
                 "title", typeof<string>, true, "Story title"
                 "why", typeof<string>, true, "One sentence: the problem this solves"
                 "loc", typeof<int>, true, "Expected changed lines (diff size budget)"
                 "story", typeof<string>, true,
                 "The full user story text, verbatim. The server reads its acceptance criteria; don't extract or rewrite them." ]
        Visibility = [ "model" ]
        Ui = false
        Run = fun a ->
            match Engine.start (repoOf a) (arg a "id") (arg a "title") (arg a "why") (arg a "loc") (arg a "story") with
            | Result.Ok p ->
                let crit =
                    if p.Criteria.IsEmpty then "story has no acceptance criteria; tests are the acceptance"
                    else "story criteria (each needs a test with covers):\n" + String.Join("\n", p.Criteria |> List.mapi (fun i c -> $"{i + 1}. {c}"))
                ok $"{summary p}\n{crit}"
            | Result.Error e -> err e }

      { Name = "plan_add"
        Description = "Append changes. Each is validated against the index; invalid ones are rejected with suggestions and valid ones kept. One change per file or symbol."
        Args = [ "id", typeof<string>, true, "Plan id"
                 "changes", typeof<Change list>, true, "Changes to append" ]
        Visibility = [ "model" ]
        Ui = false
        Run = fun a ->
            // Decode element by element so one malformed change doesn't sink the valid ones.
            let raw =
                match getProp a "changes" with
                | :? JsonArray as arr -> List.ofSeq arr
                | _ -> raise (ArgError "changes: expected array")
            let decoded =
                raw
                |> List.mapi (fun i n ->
                    match Json.ofNode<Change> n with
                    | Result.Ok c -> i, Result.Ok c
                    | Result.Error e -> i, Result.Error(e.Replace("$", $"changes[{i}]")))
            let valid = decoded |> List.choose (fun (i, r) -> match r with Result.Ok c -> Some(i, c) | _ -> None)
            let decodeErrors = decoded |> List.choose (fun (_, r) -> match r with Result.Error e -> Some e | _ -> None)
            match Engine.add (arg a "id") (valid |> List.map snd) with
            | Result.Error e -> err e
            | Result.Ok r ->
                // Engine numbers errors by position in `valid`; map back to the caller's indices.
                let remap (e: string) =
                    let m = Text.RegularExpressions.Regex.Match(e, @"^changes\[(\d+)\]")
                    if m.Success then $"changes[{fst valid[int m.Groups[1].Value]}]" + e.Substring m.Length else e
                let errors = decodeErrors @ (r.Errors |> List.map remap)
                let lines =
                    [ $"accepted {r.Accepted} of {raw.Length}; {summary r.Plan}"
                      yield! errors |> List.map (fun e -> "error " + e)
                      yield! r.Warnings |> List.map (fun w -> "warn " + w) ]
                { ok (String.Join("\n", lines)) with IsError = r.Accepted = 0 && not errors.IsEmpty } }

      { Name = "plan_remove"
        Description = "Remove the change at index (0-based)."
        Args = [ "id", typeof<string>, true, "Plan id"
                 "index", typeof<int>, true, "Change index" ]
        Visibility = [ "model"; "app" ]
        Ui = false
        Run = fun a ->
            match Engine.removeChange (arg a "id") (arg a "index") with
            | Result.Ok p -> ok (summary p)
            | Result.Error e -> err e }

      { Name = "plan_update"
        Description = "Update plan fields. standards replaces the cited rule ids; allow replaces globs the PR may touch unplanned."
        Args = [ "id", typeof<string>, true, "Plan id"
                 "title", typeof<string option>, false, "Title"
                 "why", typeof<string option>, false, "Why"
                 "loc", typeof<int option>, false, "Expected changed lines"
                 "standards", typeof<string list option>, false, "Cited standard ids"
                 "allow", typeof<string list option>, false, "Globs allowed to change without being planned" ]
        Visibility = [ "model"; "app" ]
        Ui = false
        Run = fun a ->
            match Engine.update (arg a "id") (opt a "title") (opt a "why") (opt a "loc") (opt a "standards") (opt a "allow") with
            | Result.Ok p -> ok (summary p)
            | Result.Error e -> err e }

      { Name = "plan_check"
        Description = "Run the pre-dev gates: references resolve at HEAD, altered symbols inspected, every story criterion covered by an added test, applicable standards cited."
        Args = [ "id", typeof<string>, true, "Plan id" ]
        Visibility = [ "model"; "app" ]
        Ui = true
        Run = fun a ->
            withPlan (arg a "id") (fun p ->
                let r = Engine.check p
                { Text = (if r.Ready then "READY\n" else "NOT READY\n") + findingsText r.Findings
                  View = view p (Some r)
                  IsError = false }) }

      { Name = "plan_show"
        Description = "Show the pre-PR: a diff-shaped view of every planned change for the user to review. Renders as UI where supported."
        Args = [ "id", typeof<string>, true, "Plan id" ]
        Visibility = [ "model"; "app" ]
        Ui = true
        Run = fun a ->
            withPlan (arg a "id") (fun p ->
                let r = Engine.check p
                { Text = Engine.render p; View = view p (Some r); IsError = false }) }

      { Name = "plan_publish"
        Description = "Freeze the plan once plan_check is ready. target 'repo' writes .stories/<id>.plan.json and .plan.md; 'issue' prepares it to be filed as a GitHub issue (CI); 'none' only marks it published. Locally, only after the user approves."
        Args = [ "id", typeof<string>, true, "Plan id"
                 "target", typeof<string option>, false, "'repo' (default), 'issue' or 'none'" ]
        Visibility = [ "model"; "app" ]
        Ui = false
        Run = fun a ->
            match Engine.publish (arg a "id") (opt a "target" |> Option.defaultValue "repo") with
            | Result.Ok(p, msg) -> ok $"{summary p}\n{msg}"
            | Result.Error e -> err e }

      { Name = "plan_verify"
        Description = "Post-dev gate: compare a PR (base..head) with the plan. Fails on missing or unplanned files; warns on unplanned symbol edits, signature drift, standards and size."
        Args = [ "id", typeof<string>, true, "Plan id"
                 "base", typeof<string>, true, "Base ref, e.g. main"
                 "head", typeof<string>, true, "Head ref, e.g. the PR branch"
                 "root", typeof<string option>, false, "Checkout to verify in, if not the plan's repo" ]
        Visibility = [ "model"; "app" ]
        Ui = false
        Run = fun a ->
            withPlan (arg a "id") (fun p ->
                let r = Engine.verify p (arg a "base") (arg a "head") (opt a "root")
                let verdict = if r.Conforms then "CONFORMS" else "DRIFT"
                let head = $"{p.Id} {r.Base}..{r.Head}: {verdict}"
                let stats = $"plan ~{r.PlanTokens} tokens vs diff ~{r.DiffTokens}; {r.ChangedLines} changed lines"
                { Text = $"{head}\n{findingsText r.Findings}\n{stats}"
                  View = Json.toNode r
                  IsError = false }) }

      { Name = "plan_list"
        Description = "List plans, optionally for one repo."
        Args = [ repoArg ]
        Visibility = [ "model"; "app" ]
        Ui = false
        Run = fun a ->
            let repo = opt<string> a "repo" |> Option.map Path.GetFullPath
            let plans = Engine.Store.list () |> List.filter (fun p -> repo.IsNone || String.Equals(p.Repo, repo.Value, StringComparison.OrdinalIgnoreCase))
            let arr = JsonArray()
            for p in plans do
                let o = JsonObject()
                o["id"] <- JsonValue.Create p.Id
                o["title"] <- JsonValue.Create p.Title
                o["status"] <- Json.toNode p.Status
                o["repo"] <- JsonValue.Create p.Repo
                o["changes"] <- JsonValue.Create p.Changes.Length
                arr.Add o
            { Text = (if plans.IsEmpty then "no plans" else String.Join("\n", plans |> List.map summary)); View = arr; IsError = false } } ]

let byName = all |> List.map (fun t -> t.Name, t) |> Map.ofList

let inputSchema (t: Tool) = Json.argsSchema t.Args

/// Run a tool by name. Argument and engine errors come back as error results, never exceptions.
let call (name: string) (args: JsonObject) : ToolResult =
    match byName.TryFind name with
    | None -> err $"unknown tool {name}"
    | Some t ->
        try t.Run(if isNull args then JsonObject() else args)
        with
        | ArgError m -> err m
        | ex -> err $"{name} failed: {ex.Message}"

let preplanPrompt (story: string) (id: string) =
    // The person invoking the prompt supplied this text: register it so plan_start uses it, not the agent's copy.
    Engine.Store.saveStory id story
    $"""Create a StoryPlan pre-PR for story {id}. Follow these steps exactly; the server enforces each gate.

1. plan_start id={id} with a title, a one-sentence why, a loc estimate, and story = the story below, verbatim.
   It replies with the story's acceptance criteria, numbered. Every one must be covered by a test you plan.
2. repo_map (budget 1500), then find_symbol for what the story touches. Never invent handles.
3. get_symbol plan={id} on every symbol you will alter or remove.
4. standards; plan_update standards=[every rule whose glob matches a file you plan to change].
5. plan_add every change in one call, one change per symbol:
   - new member of an existing type: extend, name Type.member
   - changed member: alter that member (never its whole type); newSig only if the signature changes
   - new file: newFile; docs/config: touch
   - test cases are new symbols named as the index names them (it('does x') -> does_x; C# [Fact] method -> Class.Method)
   - each test sets covers to the criterion numbers it proves; use the story's names for anything it names
   - sig is the exact first line of the declaration
   - does (new symbols) and change (altered ones) are ONE plain-English sentence a non-coder could follow, no code
   Fix every error it returns.
6. plan_check until READY, then plan_show and stop. Publish only after the user approves.

Keep every sentence short and plain. One change per file or symbol.

Story:
{story}"""
