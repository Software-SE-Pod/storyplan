/// Plan lifecycle: build it change by change with validation, gate it, render it, publish it, verify a PR against it.
module StoryPlan.Engine

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.RegularExpressions
open StoryPlan.Model
open StoryPlan.Symbols

type CheckReport = { Ready: bool; Findings: Finding list }

type AddReport =
    { Accepted: int
      Errors: string list
      Warnings: string list
      Plan: Plan }

type VerifyReport =
    { Base: string
      Head: string
      Conforms: bool
      Findings: Finding list
      ChangedLines: int
      PlanTokens: int
      DiffTokens: int }

let short (sha: string) = sha.Substring(0, min 7 sha.Length)
let private tokens (s: string) = s.Length / 4

// ---------- store ----------

module Store =
    let private gate = obj ()
    let dir () = Path.Combine(Standards.home (), "plans")
    let private path id = Path.Combine(dir (), id + ".json")

    let validId (id: string) = Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")

    let tryGet (id: string) : Result<Plan, string> =
        lock gate (fun () ->
            if not (validId id) then Result.Error $"invalid plan id '{id}'"
            elif not (File.Exists(path id)) then Result.Error $"no plan '{id}'; start one with plan_start"
            else Json.deserialize<Plan> (File.ReadAllText(path id)))

    let save (p: Plan) =
        lock gate (fun () ->
            Directory.CreateDirectory(dir ()) |> ignore
            File.WriteAllText(path p.Id, Json.serialize p))
        p

    let list () =
        lock gate (fun () ->
            if Directory.Exists(dir ()) then
                Directory.GetFiles(dir (), "*.json")
                |> Array.choose (fun f -> match Json.deserialize<Plan> (File.ReadAllText f) with Result.Ok p -> Some p | _ -> None)
                |> Array.sortBy (fun p -> p.Id)
                |> List.ofArray
            else [])

    let private storyPath id = Path.Combine(Standards.home (), "stories", id + ".md")

    /// Stories as a person supplied them (via the preplan prompt or the CLI), keyed by story id.
    /// plan_start prefers this over whatever text the agent passes, so criteria can't be dropped or reworded.
    let saveStory (id: string) (text: string) =
        if validId id && not (String.IsNullOrWhiteSpace text) then
            lock gate (fun () ->
                Directory.CreateDirectory(Path.GetDirectoryName(storyPath id)) |> ignore
                File.WriteAllText(storyPath id, text.Trim()))

    let tryStory (id: string) =
        lock gate (fun () -> if validId id && File.Exists(storyPath id) then Some(File.ReadAllText(storyPath id)) else None)

/// Index at a commit, with SCIP symbols wherever an index.scip covers a file (regex everywhere else).
let indexAt (root: string) rev =
    let scip = Scip.discover root
    let salt = String.Join("|", scip |> List.map (fun f -> $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}"))
    Index.get root rev (Scip.overridesFor root) salt

// ---------- validation ----------

/// Test files by common convention across languages: tests/ or spec/ folders, *.test.ts, *.spec.js,
/// FooTests.cs, foo_test.go, test_foo.py.
let isTestPath (path: string) =
    Regex.IsMatch(path, @"(^|/)(tests?|specs?|__tests__)/|[._-](test|spec)s?\.\w+$|Tests?\.(cs|fs|java|kt)$|(^|/)test_[^/]*\.py$", RegexOptions.IgnoreCase)

let private cleanCriteria (criteria: string list) =
    criteria |> List.map (fun c -> c.Trim()) |> List.filter (fun c -> c <> "")

let private criteriaHeading =
    Regex(@"^\s*(?:#{1,6}\s*|\*\*)?\s*(?:acceptance\s+criteria|acceptance|ACs?)\s*(?:\*\*)?\s*:?\s*(?:\*\*)?\s*$", RegexOptions.IgnoreCase)

let private listItem = Regex(@"^\s*(?:[-*+•]|\d+[.)]|\[[ xX]\])\s+(.*)$")
let private gherkin = Regex(@"^\s*(?:Given|When|Then|And|But)\b", RegexOptions.IgnoreCase)
let private nextHeading = Regex(@"^\s*(?:#{1,6}\s+\S|\*\*[^*]+\*\*\s*:?\s*$|[A-Z][\w /&-]{2,40}:\s*$)")

/// Acceptance criteria from story text: the list (or Given/When/Then block) under an "Acceptance criteria"
/// heading. Works for markdown issues, ADO descriptions and plain text. A wrapped line joins its item.
let parseCriteria (story: string) : string list =
    if String.IsNullOrWhiteSpace story then []
    else
        let lines = story.Replace("\r\n", "\n").Split('\n')
        match lines |> Array.tryFindIndex criteriaHeading.IsMatch with
        | None -> []
        | Some h ->
            let items = ResizeArray<string>()
            let mutable stop = false
            let mutable i = h + 1
            while not stop && i < lines.Length do
                let l = lines[i]
                let m = listItem.Match l
                if m.Success then items.Add(m.Groups[1].Value.Trim())
                elif gherkin.IsMatch l then
                    // Given/When/Then lines form one scenario; a new Given starts the next criterion.
                    if l.TrimStart().StartsWith("Given", StringComparison.OrdinalIgnoreCase) || items.Count = 0 then items.Add(l.Trim())
                    else items[items.Count - 1] <- items[items.Count - 1] + " " + l.Trim()
                elif l.Trim() = "" then
                    // A blank line ends the section only once items exist and the next text isn't another item.
                    let next = lines[i + 1 ..] |> Array.tryFind (fun x -> x.Trim() <> "")
                    if items.Count > 0 && not (next |> Option.exists (fun x -> listItem.IsMatch x || gherkin.IsMatch x)) then stop <- true
                elif nextHeading.IsMatch l then stop <- true
                elif items.Count > 0 && (l.StartsWith " " || l.StartsWith "\t") then
                    items[items.Count - 1] <- items[items.Count - 1] + " " + l.Trim()
                elif items.Count = 0 then items.Add(l.Trim())
                else stop <- true
                i <- i + 1
            cleanCriteria (List.ofSeq items)

let private codeLike = Regex(@"[;{}`]|=>|==|!=|\+\+|<-|&&|\|\||\w\.\w+\(|\w\(\)")

/// Reviewers read intent, not code: one short plain-English sentence.
let plainEnglish (label: string) (text: string) =
    let t = if isNull text then "" else text.Trim()
    if t = "" then Some $"{label}: say in one plain-English sentence what it does"
    elif t.Contains '\n' then Some $"{label}: keep it to one line"
    elif t.Length > 160 then Some $"{label}: keep it under 160 characters (it has {t.Length})"
    elif codeLike.IsMatch t then Some $"{label}: write it in plain English, not code: '{t}'"
    else None

let private validateChange (ix: Index.RepoIndex) (criteriaCount: int) (existing: Change list) (c: Change) : Result<string list, string> =
    let english (label: string) (text: string) (ok: unit -> Result<string list, string>) =
        match plainEnglish label text with
        | Some e -> Result.Error e
        | None -> ok ()
    let coversOk (file: string) (s: NewSym) =
        match s.Covers with
        | [] -> None
        | _ when not (isTestPath file) -> Some $"{s.Name}: only tests can cover criteria, and {file} isn't a test file"
        | _ when criteriaCount = 0 -> Some $"{s.Name}: the story has no criteria; omit covers"
        | cs ->
            match cs |> List.filter (fun n -> n < 1 || n > criteriaCount) with
            | [] -> None
            | bad -> Some $"{s.Name}: covers {bad} but the story has criteria 1-{criteriaCount}"
    let symsOk (file: string) (syms: NewSym list) =
        let prose = syms |> List.choose (fun s -> plainEnglish $"{s.Name}.does" s.Does)
        let covers = syms |> List.choose (coversOk file)
        if syms |> List.exists (fun s -> String.IsNullOrWhiteSpace s.Name || String.IsNullOrWhiteSpace s.Sig) then
            Result.Error $"{file}: every new symbol needs a name and the exact signature"
        elif not prose.IsEmpty then Result.Error(String.Join("; ", prose))
        elif not covers.IsEmpty then Result.Error(String.Join("; ", covers))
        else
            let clash = syms |> List.filter (fun s -> ix.ByHandle.ContainsKey(Handle.make file s.Name))
            // The planned name must be the name the index will see once the signature exists in code.
            let mismatched =
                syms
                |> List.choose (fun s ->
                    match nameFromSig file s.Name s.Sig with
                    | Some leaf when leaf <> Index.leaf s.Name ->
                        let i = s.Name.LastIndexOf '.'
                        let fixedName = if i > 0 then s.Name.Substring(0, i + 1) + leaf else leaf
                        Some $"{s.Name}: its signature declares '{leaf}', so name it '{fixedName}'"
                    | _ -> None)
            if not clash.IsEmpty then
                let names = String.Join(", ", clash |> List.map (fun s -> s.Name))
                Result.Error $"{file}: {names} already exists; use alter"
            elif not mismatched.IsEmpty then Result.Error(String.Join("; ", mismatched))
            else
                Result.Ok
                    [ for s in syms do
                          if (nameFromSig file s.Name s.Sig).IsNone && languageOf file <> "markdown" && languageOf file <> "other" then
                              $"{s.Name}: sig isn't a recognizable declaration line; use the exact first line of the declaration" ]
    let dup h =
        existing
        |> List.exists (function
            | Alter(x, _, _) | Remove(x, _) -> x = h
            | _ -> false)
    match c with
    | NewFile(p, why, syms) ->
        if Index.fileExists ix p then Result.Error $"{p} already exists; use extend or touch"
        elif existing |> List.exists (function NewFile(q, _, _) -> q = p | _ -> false) then Result.Error $"{p} is already planned"
        else
            english $"{p}.why" why (fun () ->
                symsOk p syms
                |> Result.map (fun w -> if Index.dirExists ix p then w else w @ [ $"{p}: creates a new directory" ]))
    | Extend(f, syms) ->
        if not (Index.fileExists ix f) then Result.Error $"{f} does not exist at {short ix.Sha}; use newFile"
        else
            symsOk f syms
            |> Result.map (fun w ->
                let types = Index.symbolsOf ix f |> List.map (fun s -> s.Name) |> set
                w
                @ [ for s in syms do
                        match s.Name.LastIndexOf '.' with
                        | i when i > 0 && not (types.Contains(s.Name.Substring(0, i))) ->
                            $"{s.Name}: no type '{s.Name.Substring(0, i)}' in {f}"
                        | _ -> () ])
    | Alter(h, newSig, change) ->
        match Index.resolve ix h with
        | Result.Error e -> Result.Error e
        | Result.Ok _ when dup h -> Result.Error $"{h} is already in the plan; remove it first"
        | Result.Ok _ when (plainEnglish $"{h}.change" change).IsSome -> Result.Error (plainEnglish $"{h}.change" change).Value
        | Result.Ok s when newSig |> Option.exists (fun ns -> ns.Trim() = s.Sig) -> Result.Error $"{h}: newSig equals the current signature; omit newSig"
        | Result.Ok s when s.Kind = Type && newSig.IsNone ->
            // Editing "the class" hides which members change; reviewers and verify need the member.
            let members = Index.symbolsOf ix s.File |> List.filter (fun m -> m.Name.StartsWith(s.Name + "."))
            if members.IsEmpty then Result.Ok []
            else
                let some = String.Join(", ", members |> List.truncate 4 |> List.map (fun m -> m.Handle))
                Result.Error $"{h} is a type; alter the members that change (e.g. {some}) and extend it for new members"
        | Result.Ok _ -> Result.Ok []
    | Remove(h, why) ->
        match Index.resolve ix h with
        | Result.Error e -> Result.Error e
        | Result.Ok _ when dup h -> Result.Error $"{h} is already in the plan"
        | Result.Ok _ -> english $"{h}.why" why (fun () -> Result.Ok [])
    | RemoveFile(f, why)
    | Touch(f, why) ->
        if Index.fileExists ix f then english $"{f}.why" why (fun () -> Result.Ok [])
        else Result.Error $"{f} does not exist at {short ix.Sha}"

// ---------- operations ----------

let start (repo: string) (id: string) (title: string) (why: string) (loc: int) (story: string) : Result<Plan, string> =
    // A story the person registered wins over the agent's copy.
    let story = Store.tryStory id |> Option.defaultValue story
    if not (Store.validId id) then Result.Error $"invalid plan id '{id}' (letters, digits, . _ -)"
    elif not (Git.isRepo repo) then Result.Error $"{repo} is not a git repository"
    elif String.IsNullOrWhiteSpace story then Result.Error "story: pass the full user story text, verbatim"
    else
        match Store.tryGet id with
        | Result.Ok p when p.Status <> Draft -> Result.Error $"plan {id} is {p.Status}; pick a new id"
        | _ ->
            let root = Path.GetFullPath(Git.run repo [ "rev-parse"; "--show-toplevel" ] |> fun s -> s.Trim())
            let ix = indexAt root "HEAD"
            Result.Ok(
                Store.save
                    { Id = id; Title = title; Why = why; Repo = root; Sha = ix.Sha; Story = story.Trim(); Criteria = parseCriteria story
                      Standards = []; Changes = []; Loc = loc; Allow = []; Inspected = []; Status = Draft })

let private mutate id (f: Plan -> Result<Plan * 'a, string>) =
    Store.tryGet id
    |> Result.bind (fun p ->
        if p.Status = Published then Result.Error $"plan {id} is published; start a new revision"
        else f p |> Result.map (fun (p', a) -> Store.save { p' with Status = Draft }, a))

let add id (changes: Change list) : Result<AddReport, string> =
    mutate id (fun p ->
        let ix = indexAt p.Repo p.Sha
        let mutable acc = p.Changes
        let errors = ResizeArray()
        let warnings = ResizeArray()
        changes
        |> List.iteri (fun i c ->
            match validateChange ix p.Criteria.Length acc c with
            | Result.Ok w ->
                acc <- acc @ [ c ]
                warnings.AddRange w
            | Result.Error e -> errors.Add $"changes[{i}]: {e}")
        let p' = { p with Changes = acc }
        Result.Ok(p', (changes.Length - errors.Count, List.ofSeq errors, List.ofSeq warnings)))
    |> Result.map (fun (p, (n, e, w)) -> { Accepted = n; Errors = e; Warnings = w; Plan = p })

let removeChange id (index: int) =
    mutate id (fun p ->
        if index < 0 || index >= p.Changes.Length then Result.Error $"no change at index {index}"
        else Result.Ok({ p with Changes = List.removeAt index p.Changes }, ()))
    |> Result.map fst

let update id (title: string option) (why: string option) (loc: int option) (standards: string list option) (allow: string list option) =
    mutate id (fun p ->
        Result.Ok(
            { p with
                Title = defaultArg title p.Title
                Why = defaultArg why p.Why
                Loc = defaultArg loc p.Loc
                Standards = defaultArg standards p.Standards
                Allow = defaultArg allow p.Allow },
            ()))
    |> Result.map fst

let inspect id (handle: string) =
    mutate id (fun p ->
        if List.contains handle p.Inspected then Result.Ok(p, ())
        else Result.Ok({ p with Inspected = p.Inspected @ [ handle ] }, ()))
    |> Result.map fst

/// Tests planned for each story criterion (1-based), in criterion order.
let coverage (p: Plan) =
    let tests = p.Changes |> List.collect Change.added
    p.Criteria |> List.mapi (fun i text -> i + 1, text, tests |> List.filter (fun s -> List.contains (i + 1) s.Covers))

// ---------- check: the pre-dev gate ----------

let check (p: Plan) : CheckReport =
    let rulesResult = Standards.load p.Repo
    let rules = match rulesResult with Result.Ok r -> r | _ -> []
    let head = Git.revParse p.Repo "HEAD"
    let ix = indexAt p.Repo head
    let planned = p.Changes |> List.map Change.file |> List.distinct
    let addsTests = p.Changes |> List.exists (fun c -> isTestPath (Change.file c) && not (Change.added c).IsEmpty)
    let findings =
        [ match rulesResult with
          | Result.Error e -> finding Fail "standards" e
          | _ -> ()
          if p.Changes.IsEmpty then finding Fail "empty" "plan has no changes"
          if p.Loc <= 0 then finding Fail "loc" "set loc: expected changed lines"
          if head <> p.Sha then finding Warn "moved" $"repo moved {short p.Sha} -> {short head}; references re-checked at HEAD"
          // Every reference still resolves at HEAD and nothing it adds already exists.
          for c in p.Changes do
              match validateChange ix p.Criteria.Length (p.Changes |> List.filter (fun x -> not (obj.ReferenceEquals(x, c)))) c with
              | Result.Error e -> finding Fail "ref" e
              | Result.Ok _ ->
                  match c with
                  | Alter(h, _, _) | Remove(h, _) ->
                      let atPlan = (indexAt p.Repo p.Sha).ByHandle.TryGetValue h
                      match atPlan, ix.ByHandle.TryGetValue h with
                      | (true, a), (true, b) when a.Sig <> b.Sig -> finding Warn "drift" $"{h} signature changed since planning: {b.Sig}"
                      | _ -> ()
                  | _ -> ()
          // The planner looked at what it changes.
          for c in p.Changes do
              match c with
              | Alter(h, _, _) | Remove(h, _) when not (List.contains h p.Inspected) ->
                  finding Fail "inspect" $"{h} was never inspected; call get_symbol with plan='{p.Id}'"
              | _ -> ()
          // Every criterion the story states is covered by a test the plan adds. Without criteria, the tests are the acceptance.
          for n, text, tests in coverage p do
              if tests.IsEmpty then finding Fail "criteria" $"criterion {n} has no test: '{text}'. Add a test with covers [{n}]"
          if p.Criteria.IsEmpty && not addsTests then finding Warn "tests" "plan adds no tests"
          // Every standard that applies to a planned file is cited, and every cited ID exists.
          let ids = rules |> List.map (fun r -> r.Id) |> set
          for s in p.Standards do
              if not (ids.Contains s) then finding Fail "standard" $"unknown standard {s}"
          for r in rules do
              if planned |> List.exists (Standards.globMatch r.Glob) && not (List.contains r.Id p.Standards) then
                  finding Fail "standard" $"{r.Id} applies to this plan; cite it after reading it ({r.Text})" ]
    let ready = findings |> List.forall (fun f -> f.Severity <> Fail)
    { Ready = ready
      Findings = if ready then findings @ [ finding Pass "ready" "all gates pass; plan_publish will accept it" ] else findings }

// ---------- render ----------

let render (p: Plan) =
    let ix = indexAt p.Repo p.Sha
    let sigOf h = match ix.ByHandle.TryGetValue h with | true, s -> s.Sig | _ -> h
    let code (s: string) = "`" + s.Replace("`", "'") + "`"
    let out = StringBuilder()
    let w (l: string) = out.AppendLine l |> ignore
    let byFile = p.Changes |> List.groupBy Change.file
    let mark (cs: Change list) =
        if cs |> List.exists (function NewFile _ -> true | _ -> false) then "+"
        elif cs |> List.exists (function RemoveFile _ -> true | _ -> false) then "−"
        else "~"
    w $"## {p.Id}: {p.Title}"
    if p.Why <> "" then w $"_{p.Why}_  "
    w $"Planned against `{short p.Sha}` · {byFile.Length} files · ~{p.Loc} LOC · {p.Status}"
    if not p.Criteria.IsEmpty then
        w ""
        w "**Story criteria**"
        for n, text, tests in coverage p do
            match tests with
            | [] -> w $"{n}. {text} ✗ no test"
            | ts -> w ($"{n}. {text} ✓ " + String.Join(", ", ts |> List.map (fun t -> code (Index.leaf t.Name))))
    let covers (s: NewSym) =
        if s.Covers.IsEmpty then "" else " _(covers " + String.Join(", ", s.Covers) + ")_"
    for path, changes in byFile do
        w ""
        let fileWhy = changes |> List.tryPick (function NewFile(_, why, _) | RemoveFile(_, why) -> Some why | _ -> None)
        w ($"**{mark changes} {path}**" + (fileWhy |> Option.map (fun y -> ": " + y) |> Option.defaultValue ""))
        for c in changes do
            match c with
            | NewFile(_, _, syms)
            | Extend(_, syms) ->
                for n in syms do w $"- + {code n.Sig}: {n.Does}{covers n}"
            | Alter(h, Some ns, change) -> w $"- ~ {code (sigOf h)} → {code ns}: {change}"
            | Alter(h, None, change) -> w $"- ~ {code (sigOf h)}: {change}"
            | Remove(h, why) -> w $"- − {code (sigOf h)}: {why}"
            | RemoveFile _ -> ()
            | Touch(_, why) -> w $"- ~ {why}"
    if not p.Standards.IsEmpty then
        w ""
        w ("**Standards** " + String.Join(" · ", p.Standards |> List.map (fun s -> $"`{s}`")))
    let touched = p.Changes |> List.choose (function Alter(h, _, _) | Remove(h, _) -> Some h | _ -> None)
    if not touched.IsEmpty then
        w ""
        w "**Blast radius**"
        for h in touched do
            match ix.ByHandle.TryGetValue h with
            | true, s -> w $"- `{s.Name}`: referenced from {(Index.references ix s).Length} other files"
            | _ -> ()
    out.ToString()

// ---------- publish ----------

/// https://github.com/<owner>/<repo> for the plan's repo, when its origin is on GitHub.
let private githubUrl (repo: string) =
    match Git.tryRun repo [ "remote"; "get-url"; "origin" ] with
    | Some url ->
        let m = Regex.Match(url.Trim(), @"github\.com[:/]([^/\s]+)/([^/\s]+?)(?:\.git)?$")
        if m.Success then Some $"https://github.com/{m.Groups[1].Value}/{m.Groups[2].Value}" else None
    | None -> None

/// "4 files" from an exact (SCIP) index; "~4 files" when estimated by name.
let private usedIn (ix: Index.RepoIndex) (s: Sym) =
    let n = (Index.references ix s).Length
    let approx = if s.Refs.IsSome then "" else "~"
    match n with 0 -> "—" | 1 -> $"{approx}1 file" | n -> $"{approx}{n} files"

/// GitHub issue body: the same plan as render, laid out with what GitHub draws natively
/// (alerts, Mermaid, tables, diff blocks, task lists, collapsible sections). Plan data rides along at the end.
let renderIssue (p: Plan) =
    let ix = indexAt p.Repo p.Sha
    let report = check p
    let web = githubUrl p.Repo
    let out = StringBuilder()
    let w (l: string) = out.AppendLine l |> ignore
    // A code span that survives backticks and table pipes in signatures.
    let code (s: string) =
        let s = s.Replace("|", "\\|")
        let ticks = Regex.Matches(s, "`+") |> Seq.map (fun m -> m.Length) |> Seq.fold max 0
        let fence = String('`', ticks + 1)
        if ticks = 0 then fence + s + fence else $"{fence} {s} {fence}"
    let cell (s: string) = s.Replace("|", "\\|").Replace("\n", " ")
    let fileLink (path: string) =
        match web with
        | Some u -> $"[`{path}`]({u}/blob/{p.Sha}/{path})"
        | None -> $"`{path}`"
    let lineLink (path: string) (line: int) (text: string) =
        match web with
        | Some u -> $"[{text}]({u}/blob/{p.Sha}/{path}#L{line})"
        | None -> text
    let sym h = match ix.ByHandle.TryGetValue h with | true, s -> Some s | _ -> None
    let testTitle (s: NewSym) =
        let m = Regex.Match(s.Sig, @"^\s*(?:it|test)(?:\.\w+)?\s*\(\s*(['""`])(.+?)\1")
        if m.Success then m.Groups[2].Value else (Index.leaf s.Name).Replace('_', ' ')
    let isTest (file: string) (s: NewSym) = isTestPath file && (not s.Covers.IsEmpty || s.Sig.Contains "it(" || s.Sig.Contains "test(" || s.Sig.Contains "[Fact" || s.Name.Contains "Test")
    let byFile = p.Changes |> List.groupBy Change.file
    let fileKind (cs: Change list) =
        if cs |> List.exists (function NewFile _ -> true | _ -> false) then "add"
        elif cs |> List.exists (function RemoveFile _ -> true | _ -> false) then "del"
        else "mod"
    let dot = function "add" -> "🟢" | "del" -> "🔴" | _ -> "🟡"
    let fails = report.Findings |> List.filter (fun f -> f.Severity = Fail)
    let warns = report.Findings |> List.filter (fun f -> f.Severity = Warn)
    let short7 = short p.Sha
    let shaLink = match web with Some u -> $"[`{short7}`]({u}/commit/{p.Sha})" | None -> $"`{short7}`"

    // Summary banner
    let status = if report.Ready then "ready for review" else $"**not ready**: {fails.Length} failing checks"
    let alert = if report.Ready then "NOTE" else "WARNING"
    w $"> [!{alert}]"
    w $"> **Pre-PR for {p.Id}**: {p.Why}"
    w $"> Planned against {shaLink} · {byFile.Length} files · ~{p.Loc} lines · {status}"
    w ""
    if p.Story <> "" then
        w "<details><summary><b>Original story</b></summary>"
        w ""
        for l in p.Story.Replace("\r\n", "\n").Split('\n') do w ("> " + l)
        w ""
        w "</details>"
        w ""

    // Map: criteria on the left point at the tests that prove them; files hold their symbols.
    let mm (s: string) = s.Replace("\"", "#quot;").Replace("<", "#lt;").Replace(">", "#gt;")
    let clip (s: string) = if s.Length > 48 then s.Substring(0, 45) + "…" else s
    w "### Map"
    w ""
    w "```mermaid"
    w "flowchart LR"
    w "  classDef add fill:#dafbe1,stroke:#1a7f37,color:#1f2328"
    w "  classDef mod fill:#fff8c5,stroke:#9a6700,color:#1f2328"
    w "  classDef del fill:#ffebe9,stroke:#cf222e,color:#1f2328"
    w "  classDef ac fill:#ddf4ff,stroke:#0969da,color:#1f2328"
    w "  classDef test fill:#dafbe1,stroke:#1a7f37,color:#1f2328,stroke-dasharray:4 3"
    let nodeIds = Collections.Generic.Dictionary<string, string>()
    byFile
    |> List.iteri (fun fi (path, cs) ->
        w $"  subgraph f{fi}[\"{mm (Path.GetFileName path)}\"]"
        cs
        |> List.iteri (fun ci c ->
            let id = $"n{fi}_{ci}"
            match c with
            | NewFile(_, _, syms) | Extend(_, syms) ->
                syms
                |> List.iteri (fun si s ->
                    let sid = $"{id}_{si}"
                    nodeIds[s.Name] <- sid
                    if isTest path s then w $"    {sid}[\"🧪 {mm (clip (testTitle s))}\"]:::test"
                    else w $"    {sid}[\"+ {mm (Index.leaf s.Name)}\"]:::add")
            | Alter(h, _, _) -> w $"    {id}[\"~ {mm (Handle.split h |> Option.map snd |> Option.defaultValue h)}\"]:::mod"
            | Remove(h, _) -> w $"    {id}[\"− {mm (Handle.split h |> Option.map snd |> Option.defaultValue h)}\"]:::del"
            | RemoveFile _ -> w $"    {id}[\"− whole file\"]:::del"
            | Touch(_, why) -> w $"    {id}[\"~ {mm (clip why)}\"]:::mod")
        w "  end")
    for n, _, tests in coverage p do
        w $"  ac{n}([\"AC {n}\"]):::ac"
        for t in tests do
            match nodeIds.TryGetValue t.Name with
            | true, sid -> w $"  ac{n} --> {sid}"
            | _ -> ()
    w "```"
    w ""

    // Acceptance criteria and their proof
    if not p.Criteria.IsEmpty then
        w "### Acceptance criteria"
        w ""
        w "| # | Criterion | Proved by |"
        w "|:-:|---|---|"
        let testFile (t: NewSym) =
            p.Changes |> List.tryPick (fun c -> if List.contains t (Change.added c) then Some(Change.file c) else None)
        for n, text, tests in coverage p do
            let proof =
                match tests with
                | [] -> "❌ no test"
                | ts -> "✅ " + String.Join("<br>", ts |> List.map (fun t -> cell (testTitle t)))
            w $"| {n} | {cell text} | {proof} |"
        w ""

    // Changes, one table per file
    w "### Changes"
    w ""
    for path, cs in byFile do
        let kind = fileKind cs
        let fileWhy = cs |> List.tryPick (function NewFile(_, why, _) | RemoveFile(_, why) -> Some why | _ -> None)
        w ($"{dot kind} {fileLink path}" + (fileWhy |> Option.map (fun y -> " · " + cell y) |> Option.defaultValue ""))
        w ""
        let rows = ResizeArray<string>()
        let sigDiffs = ResizeArray<string * string * string>()
        for c in cs do
            match c with
            | NewFile(_, _, syms) | Extend(_, syms) ->
                for s in syms do
                    let covers = if s.Covers.IsEmpty then "" else String.Join(", ", s.Covers |> List.map (fun n -> $"AC {n}"))
                    if isTest path s then rows.Add $"| 🧪 | {cell (testTitle s)} | {cell s.Does} | {covers} | |"
                    else rows.Add $"| ➕ | {code s.Sig} | {cell s.Does} | {covers} | |"
            | Alter(h, newSig, change) ->
                let s = sym h
                let label = s |> Option.map (fun s -> code s.Sig) |> Option.defaultValue (code h)
                let label = match s with Some s -> lineLink s.File s.Decl label | None -> label
                let used = s |> Option.map (fun s -> usedIn ix s) |> Option.defaultValue ""
                rows.Add $"| ✏️ | {label} | {cell change} | | {used} |"
                match newSig, s with
                | Some ns, Some s -> sigDiffs.Add(Index.leaf s.Name, s.Sig, ns)
                | _ -> ()
            | Remove(h, why) ->
                let s = sym h
                let label = s |> Option.map (fun s -> code s.Sig) |> Option.defaultValue (code h)
                let used = s |> Option.map (fun s -> usedIn ix s) |> Option.defaultValue ""
                rows.Add $"| ➖ | {label} | {cell why} | | {used} |"
            | RemoveFile _ -> rows.Add "| ➖ | whole file | Deleted. | | |"
            | Touch(_, why) -> rows.Add $"| ✏️ | | {cell why} | | |"
        w "| | Change | What it does | Covers | Used in |"
        w "|:-:|---|---|:-:|:-:|"
        rows |> Seq.iter w
        if sigDiffs.Count > 0 then
            w ""
            w "<details><summary>Signature changes</summary>"
            w ""
            w "```diff"
            for name, before, after in sigDiffs do
                w $"@@ {name} @@"
                w $"- {before}"
                w $"+ {after}"
            w "```"
            w ""
            w "</details>"
        w ""

    // Pre-flight checklist
    let failing code = fails |> List.exists (fun f -> f.Code = code)
    let check (ok: bool) (text: string) = w (if ok then $"- [x] {text}" else $"- [ ] **{text}**")
    w "### Pre-flight checks"
    w ""
    check (not (failing "ref")) $"Every symbol exists at `{short7}`"
    check (not (failing "inspect")) "Every changed symbol was read before planning"
    if not p.Criteria.IsEmpty then
        let covered = coverage p |> List.filter (fun (_, _, t) -> not t.IsEmpty) |> List.length
        check (not (failing "criteria")) $"Every acceptance criterion has a test ({covered}/{p.Criteria.Length})"
    let standards = if p.Standards.IsEmpty then "none apply" else String.Join(", ", p.Standards |> List.map (fun s -> $"`{s}`"))
    check (not (failing "standard" || failing "standards")) $"Coding standards cited: {standards}"
    for f in fails |> List.filter (fun f -> not (List.contains f.Code [ "ref"; "inspect"; "criteria"; "standard"; "standards" ])) do
        check false f.Message
    if not warns.IsEmpty then
        w ""
        w "> [!WARNING]"
        for f in warns do w $"> {cell f.Message}  "
    w ""
    w "<sub>When the PR opens, CI compares it with this plan and fails on missing or unplanned changes.</sub>"
    out.ToString()

/// The issue-body form of a plan: the rendered pre-PR, then the plan JSON in a fenced block tagged
/// `json storyplan-v1` so CI can read it back. GitHub highlights it as JSON; no HTML comment needed.
module IssueBody =
    let tag = "storyplan-v1"

    let private fenceFor (json: string) =
        let longest = Regex.Matches(json, "`+") |> Seq.map (fun m -> m.Length) |> Seq.fold max 0
        String('`', max 3 (longest + 1))

    let build (markdown: string) (plan: Plan) =
        let json = Json.serialize plan
        let fence = fenceFor json
        $"{markdown.TrimEnd()}\n\n<details><summary>Plan data (read by CI, don't edit)</summary>\n\n{fence}json {tag}\n{json}\n{fence}\n\n</details>\n"

    /// The plan embedded in an issue body, if there is one.
    let extract (body: string) : Result<Plan, string> =
        let text = body.Replace("\r\n", "\n")
        let m = Regex.Match(text, @"(?m)^(`{3,})json " + Regex.Escape tag + @"\n([\s\S]*?)\n\1[ \t]*$")
        if not m.Success then Result.Error $"no StoryPlan plan in this text (missing a `json {tag}` block)"
        else Json.deserialize<Plan> m.Groups[2].Value

let publish id (target: string) : Result<Plan * string, string> =
    Store.tryGet id
    |> Result.bind (fun p ->
        let report = check p
        if not report.Ready then
            let fails = report.Findings |> List.filter (fun f -> f.Severity = Fail) |> List.map (fun f -> f.Message)
            Result.Error("not ready:\n- " + String.Join("\n- ", fails))
        else
            let p = Store.save { p with Status = Published }
            let md = render p
            match target with
            | "repo" ->
                let dir = Path.Combine(p.Repo, ".stories")
                Directory.CreateDirectory dir |> ignore
                File.WriteAllText(Path.Combine(dir, p.Id + ".plan.json"), Json.serialize p)
                File.WriteAllText(Path.Combine(dir, p.Id + ".plan.md"), md)
                Result.Ok(p, $"wrote .stories/{p.Id}.plan.json and .plan.md in {p.Repo}")
            | "issue" ->
                // The finished issue body (title in .title), filed by a deterministic CI job (see gh-aw/shared/storyplan.md).
                let dir = Path.Combine(Standards.home (), "issues")
                Directory.CreateDirectory dir |> ignore
                File.WriteAllText(Path.Combine(dir, p.Id + ".md"), IssueBody.build (renderIssue p) p)
                File.WriteAllText(Path.Combine(dir, p.Id + ".title"), $"[pre-PR] {p.Id}: {p.Title}")
                Result.Ok(p, $"pre-PR for {p.Id} is ready to be filed as an issue; call publish_storyplan with plan_id={p.Id}")
            | _ -> Result.Ok(p, md))

// ---------- verify: the post-dev gate ----------

type private Hunk = { NewStart: int; NewCount: int }

let private hunkRx = Regex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")

let private hunks (diff: string) =
    let mutable file = ""
    [ for l in diff.Replace("\r\n", "\n").Split('\n') do
          if l.StartsWith "+++ " then file <- (if l = "+++ /dev/null" then "" else l.Substring 6)
          else
              let m = hunkRx.Match l
              if m.Success && file <> "" then
                  yield file, { NewStart = int m.Groups[1].Value; NewCount = (if m.Groups[2].Success then int m.Groups[2].Value else 1) } ]

let verify (p: Plan) (baseRef: string) (headRef: string) (root: string option) : VerifyReport =
    let root = defaultArg root p.Repo
    let head = Git.revParse root headRef
    let base' = Git.mergeBase root (Git.revParse root baseRef) head
    let ixHead = indexAt root head
    let status = Git.nameStatus root base' head
    let diffText = Git.diff root base' head 0
    let byFile = hunks diffText |> List.groupBy fst |> Map.ofList |> Map.map (fun _ v -> List.map snd v)
    let rules = match Standards.load root with Result.Ok r -> r | _ -> []
    let lineOf path = match ixHead.Lines.TryGetValue path with | true, l -> l | _ -> [||]
    let changed path =
        let text = lineOf path
        byFile.TryFind path
        |> Option.defaultValue []
        |> List.collect (fun h -> [ h.NewStart .. h.NewStart + h.NewCount - 1 ])
        |> List.filter (fun i -> i >= 1 && i <= text.Length && text[i - 1].Trim() <> "")
    let touched path =
        let syms = Index.symbolsOf ixHead path
        changed path |> List.choose (owner syms) |> List.map (fun s -> s.Name) |> List.distinct
    let has path name = ixHead.ByHandle.ContainsKey(Handle.make path name)
    let planned = p.Changes |> List.map Change.file |> set
    let plannedSyms =
        p.Changes
        |> List.collect (function
            | NewFile(f, _, n) | Extend(f, n) -> n |> List.map (fun x -> f, x.Name)
            | Alter(h, _, _) | Remove(h, _) -> Handle.split h |> Option.toList
            | _ -> [])
        |> set
    let findings =
        [ for c in p.Changes do
              match c with
              | NewFile(f, _, syms) ->
                  if status.TryFind f <> Some "A" then finding Fail "missing" $"+ {f}"
                  else
                      for n in syms do
                          if has f n.Name then finding Pass "done" $"+ {n.Name}" else finding Fail "missing" $"+ {n.Name} in {f}"
              | Extend(f, syms) ->
                  for n in syms do
                      if has f n.Name then finding Pass "done" $"+ {n.Name}" else finding Fail "missing" $"+ {n.Name} in {f}"
              | Alter(h, newSig, _) ->
                  match Handle.split h with
                  | Some(f, name) ->
                      match ixHead.ByHandle.TryGetValue h with
                      | false, _ -> finding Fail "missing" $"~ {name} no longer exists"
                      | true, s when not (List.contains name (touched f)) -> finding Fail "missing" $"~ {name} was not modified"
                      | true, s ->
                          match newSig with
                          | Some ns when signature ns <> s.Sig && ns.Trim() <> s.Sig -> finding Warn "sig" $"~ {name}: planned `{ns}`, got `{s.Sig}`"
                          | _ -> finding Pass "done" $"~ {name}"
                  | None -> finding Fail "missing" $"~ {h} is not a handle"
              | Remove(h, _) ->
                  if ixHead.ByHandle.ContainsKey h then finding Fail "missing" $"- {h} still exists" else finding Pass "done" $"- {h}"
              | RemoveFile(f, _) ->
                  if status.TryFind f = Some "D" then finding Pass "done" $"- {f}" else finding Fail "missing" $"- {f}"
              | Touch(f, _) ->
                  if status.ContainsKey f then finding Pass "done" $"~ {f}" else finding Fail "missing" $"~ {f}"
          // Scope creep: files and symbols nobody planned.
          for KeyValue(path, _) in status do
              if not (planned.Contains path) && not (p.Allow |> List.exists (fun g -> Standards.globMatch g path)) then
                  finding Fail "unplanned" $"file {path}"
          for path in planned do
              let touchOnly = p.Changes |> List.forall (function Touch(f, _) when f = path -> true | c -> Change.file c <> path)
              if status.TryFind path = Some "M" && not touchOnly then
                  for name in touched path do
                      if not (plannedSyms.Contains(path, name)) then finding Warn "unplanned" $"edit to {name} in {path}"
          // Standards, on changed lines only.
          for KeyValue(path, st) in status do
              if st <> "D" then
                  for r in rules do
                      if Standards.globMatch r.Glob path then
                          for msg in Standards.evaluate r path (lineOf path) (changed path) do
                              finding Warn "standard" $"{r.Id}: {msg}"
          let loc = Git.changedLineCount root base' head
          if p.Loc > 0 && float loc > 1.5 * float p.Loc then finding Warn "size" $"{loc} changed lines vs ~{p.Loc} planned" ]
    { Base = short base'
      Head = short head
      Conforms = findings |> List.forall (fun f -> f.Severity <> Fail)
      Findings = findings
      ChangedLines = Git.changedLineCount root base' head
      PlanTokens = tokens (Json.serializeCompact p)
      DiffTokens = tokens (Git.diff root base' head 3) }
