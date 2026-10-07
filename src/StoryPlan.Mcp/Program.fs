/// Command line: the MCP server (stdio or HTTP), the local web UI, schema export, and the CI verify gate.
module StoryPlan.Mcp.Program

open System
open System.IO
open System.Text.Json
open StoryPlan
open StoryPlan.Model

let usage () =
    eprintfn """storyplan %s
  storyplan [mcp] [--repo <path>] [--home <path>]  MCP server on stdio (default)
  storyplan mcp --http [--host 127.0.0.1] [--port 8766]
                                                   MCP server over streamable HTTP at /mcp (no auth: trusted networks only)
  storyplan serve [--port 5199]                    web UI + MCP Apps harness on 127.0.0.1
  storyplan schema [plan|change|standards|tools]   JSON Schema generated from the F# model
  storyplan show <id> [--issue]                    print a plan as markdown (--issue: the GitHub issue body)
  storyplan preplan <id> <story.md>                register a story and print the agent prompt for it
  storyplan verify <id|plan.json|issue.md> <base> <head> [--root <checkout>]
                                                   CI gate; exit 1 when the PR drifts from the plan

  --repo and --home set STORYPLAN_REPO and STORYPLAN_HOME (useful when env can't be passed, e.g. containers).""" Server.version

let private flag (name: string) (args: string list) =
    match args |> List.tryFindIndex ((=) name) with
    | Some i when i + 1 < args.Length -> Some args[i + 1], List.removeManyAt i 2 args
    | _ -> None, args

let private pretty (n: Nodes.JsonNode) = n.ToJsonString(JsonSerializerOptions(WriteIndented = true))

/// A plan id from the store, a plan JSON file, or any text file (e.g. a GitHub issue body) that embeds one.
let private loadPlan (idOrFile: string) =
    if File.Exists idOrFile then
        let text = File.ReadAllText idOrFile
        if text.TrimStart().StartsWith "{" then Json.deserialize<Plan> text else Engine.IssueBody.extract text
    else Engine.Store.tryGet idOrFile

[<EntryPoint>]
let main argv =
    let repo, args = flag "--repo" (List.ofArray argv)
    let home, args = flag "--home" args
    repo |> Option.iter (fun r -> Environment.SetEnvironmentVariable("STORYPLAN_REPO", r))
    home |> Option.iter (fun h -> Environment.SetEnvironmentVariable("STORYPLAN_HOME", h))
    match args with
    | [] | [ "mcp" ] ->
        (Server.run [||]).GetAwaiter().GetResult()
        0
    | "mcp" :: "--http" :: rest ->
        let host, rest = flag "--host" rest
        let port, _ = flag "--port" rest
        (Server.runHttp (defaultArg host "127.0.0.1") (port |> Option.map int |> Option.defaultValue 8766)).GetAwaiter().GetResult()
        0
    | "serve" :: rest ->
        let port, _ = flag "--port" rest
        (Web.run (port |> Option.map int |> Option.defaultValue 5199)).GetAwaiter().GetResult()
        0
    | [ "schema" ] | [ "schema"; "plan" ] -> printfn "%s" (pretty (Json.schemaOf typeof<Plan>)); 0
    | [ "schema"; "change" ] -> printfn "%s" (pretty (Json.schemaOf typeof<Change>)); 0
    | [ "schema"; "standards" ] -> printfn "%s" (pretty (Json.schemaOf typeof<Rule list>)); 0
    | [ "schema"; "tools" ] ->
        for t in Tools.all do
            printfn "## %s\n%s\n" t.Name (pretty (Tools.inputSchema t))
        0
    | [ "show"; id ] ->
        match loadPlan id with
        | Result.Ok p -> printf "%s" (Engine.render p); 0
        | Result.Error e -> eprintfn "%s" e; 1
    | [ "show"; id; "--issue" ] ->
        match loadPlan id with
        | Result.Ok p -> printf "%s" (Engine.IssueBody.build (Engine.renderIssue p) p); 0
        | Result.Error e -> eprintfn "%s" e; 1
    | [ "preplan"; id; storyFile ] ->
        if not (File.Exists storyFile) then eprintfn "no such file: %s" storyFile; 2
        else
            // Registers the story as written, then prints the agent prompt (pipe it into your agent).
            printf "%s" (Tools.preplanPrompt (File.ReadAllText storyFile) id)
            0
    | "verify" :: rest ->
        let root, rest = flag "--root" rest
        match rest with
        | [ idOrFile; b; h ] ->
            match loadPlan idOrFile with
            | Result.Error e -> eprintfn "%s" e; 2
            | Result.Ok p ->
                // A plan read from a file (CI) verifies the checkout it sits in unless told otherwise.
                let root = root |> Option.orElse (if File.Exists idOrFile then Some(Directory.GetCurrentDirectory()) else None)
                let r = Engine.verify p b h root
                printfn "%s %s..%s: %s" p.Id r.Base r.Head (if r.Conforms then "CONFORMS" else "DRIFT")
                for f in r.Findings do
                    printfn "  %-5s %-10s %s" ((string f.Severity).ToLowerInvariant()) f.Code f.Message
                printfn "plan ~%d tokens vs diff ~%d tokens; %d changed lines" r.PlanTokens r.DiffTokens r.ChangedLines
                if r.Conforms then 0 else 1
        | _ -> usage (); 2
    | _ ->
        usage ()
        2
