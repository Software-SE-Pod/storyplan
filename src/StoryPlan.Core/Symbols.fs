/// Language adapters. Each turns one file into SCIP-shaped symbols: a stable name, a kind, a one-line
/// signature and a line span. The regex adapters below cover C#, F# and TypeScript with zero setup;
/// a SCIP index (Scip.fs) replaces them per file when one is available, for any language that has an indexer.
module StoryPlan.Symbols

open System
open System.IO
open System.Text.RegularExpressions
open StoryPlan.Model

type Sym =
    { Handle: string
      File: string
      /// Qualified name inside the file, e.g. "OrderService.Place".
      Name: string
      Kind: Kind
      Sig: string
      /// First line including leading doc comments and attributes (1-based).
      Start: int
      /// Declaration line (1-based).
      Decl: int
      /// Last line, when the backend knows it (SCIP enclosing_range). 0 = until the next symbol.
      End: int
      Backend: string
      /// Files that reference this symbol, when the backend knows exactly (SCIP). None = estimate by name.
      Refs: string list option }

let splitLines (s: string) = s.Replace("\r\n", "\n").Split('\n')

let private csType =
    Regex(@"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|internal|private|protected|sealed|static|abstract|partial|readonly|file|ref)\s+)*(?:class|record|struct|interface|enum)\s+(\w+)")

let private csMember =
    Regex(@"^\s*(?:(?:public|internal|private|protected|static|virtual|override|async|sealed|abstract|readonly|new|extern|required|const)\s+)+(?!class\b|record\b|struct\b|interface\b|enum\b)[\w<>\[\]?,\.\s()]*?\b(\w+)\s*(?:<[^>()]*>)?\s*(?:\(|\{|=>|=|;)")

let private csTestAttr = Regex(@"^\s*\[(?:Fact|Theory|Test|TestMethod|TestCase)")
let private csTestMethod = Regex(@"^\s*(?:public\s+)?(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(")

let private fsDecl =
    Regex(@"^(\s*)(?:static\s+member|abstract\s+member|override|member|let|type|module)\s+(?:(?:rec|inline|private|internal|mutable)\s+)*(?:\w+\.)?(``[^`]+``|\w+)")

let private tsDecl =
    Regex(@"^\s*export\s+(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?:async\s+)?(function|const|let|class|interface|type|enum)\s+(\w+)")

let private tsTest = Regex(@"^\s*(?:it|test)(?:\.\w+)?\s*\(\s*['""`]([^'""`]+)['""`]")

let private tsLocalDecl =
    Regex(@"^\s*(?:declare\s+)?(?:abstract\s+)?(?:async\s+)?(function|const|let|class|interface|type|enum)\s+([A-Za-z_$][\w$]*)")

let private tsMember =
    Regex(@"^\s+(?:(?:public|private|protected|static|readonly|async|abstract|override|declare|get|set)\s+)*(#?[A-Za-z_$][\w$]*)\s*(?:<[^>()]*>)?\s*\??\s*[(:]")

let private tsKeywords = set [ "if"; "for"; "while"; "switch"; "return"; "catch"; "case"; "default"; "else"; "do"; "try"; "function"; "await"; "throw"; "new"; "super"; "this" ]

/// Test-case names become identifiers: "returns 0 when empty" -> returns_0_when_empty.
let testName (title: string) = Regex.Replace(title.Trim(), @"[^\w]+", "_").Trim('_')

let private stripNoise = Regex(@"""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)'|//.*$")

let private braceDelta (l: string) =
    let s = stripNoise.Replace(l, "")
    (s |> Seq.filter ((=) '{') |> Seq.length) - (s |> Seq.filter ((=) '}') |> Seq.length)

let private isLead (l: string) =
    let t = l.TrimStart()
    t.StartsWith "///" || t.StartsWith "//" || t.StartsWith "[" || t.StartsWith "/**" || t.StartsWith "*" || t.StartsWith "@"

let signature (l: string) =
    let t = l.Trim()
    let cuts =
        [ t.IndexOf " =>"; t.IndexOf " {"; (if t.EndsWith "{" then t.Length - 1 else -1) ]
        |> List.filter (fun i -> i > 0)
    (match cuts with
     | [] -> t
     | c -> t.Substring(0, List.min c)).TrimEnd(';', ' ')

let languageOf (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".cs" -> "csharp"
    | ".fs" | ".fsx" | ".fsi" -> "fsharp"
    | ".ts" | ".tsx" | ".mts" | ".cts" -> "typescript"
    | ".js" | ".jsx" | ".mjs" | ".cjs" -> "javascript"
    | ".py" -> "python"
    | ".go" -> "go"
    | ".java" -> "java"
    | ".rs" -> "rust"
    | ".razor" | ".cshtml" -> "razor"
    | ".md" -> "markdown"
    | _ -> "other"

/// Regex extraction for one file.
let extract (path: string) (ls: string[]) : Sym list =
    let found = ResizeArray<Sym>()
    let add name kind (i: int) =
        let mutable s = i
        while s > 0 && isLead ls[s - 1] do s <- s - 1
        found.Add
            { Handle = $"{path}#{name}"; File = path; Name = name; Kind = kind; Sig = signature ls[i]
              Start = s + 1; Decl = i + 1; End = 0; Backend = "regex"; Refs = None }
    match languageOf path with
    | "csharp" ->
        let mutable depth = 0
        // (qualified type name, depth of its declaration line, body opened yet)
        let mutable scopes: (string * int * bool) list = []
        let enclosing d = scopes |> List.tryFind (fun (_, td, _) -> td = d - 1) |> Option.map (fun (n, _, _) -> n)
        let opensBody i =
            braceDelta ls[i] > 0
            || (ls[i + 1 ..] |> Array.tryFind (fun l -> l.Trim() <> "") |> Option.exists (fun l -> l.Trim().StartsWith "{"))
        let mutable pendingTest = false
        for i in 0 .. ls.Length - 1 do
            let l = ls[i]
            scopes <- scopes |> List.filter (fun (_, td, opened) -> td < depth || not opened)
            let t = csType.Match l
            if t.Success then
                let name =
                    match enclosing depth with
                    | Some o -> $"{o}.{t.Groups[1].Value}"
                    | None -> t.Groups[1].Value
                if opensBody i then scopes <- (name, depth, false) :: scopes
                add name Type i
            elif csTestAttr.IsMatch l then pendingTest <- true
            else
                let tm = csTestMethod.Match l
                match enclosing depth with
                | Some o when pendingTest && tm.Success ->
                    pendingTest <- false
                    add $"{o}.{tm.Groups[1].Value}" Member i
                | Some o ->
                    let m = csMember.Match l
                    if m.Success && not (o.EndsWith m.Groups[1].Value) then add $"{o}.{m.Groups[1].Value}" Member i
                | None -> ()
            depth <- depth + braceDelta l
            scopes <- scopes |> List.map (fun (n, td, opened) -> n, td, opened || depth > td)
    | "fsharp" ->
        let mutable typ = ""
        // Indent of the enclosing module; module-level lets sit 4 deeper. Top-level `module X` / namespaces = -4.
        let mutable modules: int list = [ -4 ]
        for i in 0 .. ls.Length - 1 do
            let m = fsDecl.Match ls[i]
            if m.Success && m.Groups[1].Value.Length <= 8 then
                let indent = m.Groups[1].Value.Length
                let name = m.Groups[2].Value.Trim('`')
                let t = ls[i].TrimStart()
                modules <- modules |> List.filter (fun mi -> mi < indent || mi = -4)
                if t.StartsWith "module" then
                    if t.TrimEnd().EndsWith "=" then modules <- indent :: modules
                    typ <- name
                    add name Type i
                elif t.StartsWith "type" then
                    typ <- name
                    add name Type i
                elif t.Contains "member" || t.StartsWith "override" then add $"{typ}.{name}" Member i
                elif indent = List.head modules + 4 then add name Func i
    | "typescript" | "javascript" ->
        let mutable depth = 0
        let mutable scopes: (string * int * bool) list = []
        let opensBody i =
            braceDelta ls[i] > 0
            || (ls[i + 1 ..] |> Array.tryFind (fun l -> l.Trim() <> "") |> Option.exists (fun l -> l.Trim().StartsWith "{"))
        for i in 0 .. ls.Length - 1 do
            let l = ls[i]
            scopes <- scopes |> List.filter (fun (_, td, opened) -> td < depth || not opened)
            let inType = scopes |> List.tryFind (fun (_, td, _) -> td = depth - 1)
            let t = tsTest.Match l
            if t.Success then add (testName t.Groups[1].Value) Func i
            else
                let m = tsDecl.Match l
                let local = if m.Success then m else tsLocalDecl.Match l
                if local.Success && (m.Success || depth = 0) then
                    let k =
                        match local.Groups[1].Value with
                        | "class" | "interface" | "type" | "enum" -> Type
                        | "function" -> Func
                        | _ -> Value
                    let name = local.Groups[2].Value
                    if k = Type && opensBody i then scopes <- (name, depth, false) :: scopes
                    add name k i
                else
                    match inType with
                    | Some(owner, _, _) ->
                        let mm = tsMember.Match l
                        if mm.Success && not (tsKeywords.Contains mm.Groups[1].Value) then
                            add $"{owner}.{mm.Groups[1].Value}" Member i
                    | None -> ()
            depth <- depth + braceDelta l
            scopes <- scopes |> List.map (fun (n, td, opened) -> n, td, opened || depth > td)
    | _ -> ()
    // Overloads share a name; keep handles unique by suffixing the declaration line.
    let seen = Collections.Generic.HashSet<string>()
    [ for s in found do
          if seen.Add s.Name then s
          else { s with Name = $"{s.Name}@{s.Decl}"; Handle = $"{path}#{s.Name}@{s.Decl}" } ]

/// The symbol that owns a line: the last one starting at or before it (and not ended before it).
let owner (syms: Sym list) (line: int) =
    syms
    |> List.filter (fun s -> s.Start <= line && (s.End = 0 || line <= s.End))
    |> List.tryLast

/// The leaf name the regex adapter would index for a declaration line, so a planned name can be checked
/// against its own signature (otherwise verify would later report the symbol as missing).
let nameFromSig (path: string) (qualified: string) (sigLine: string) : string option =
    let ownerLeaf =
        match qualified.LastIndexOf '.' with
        | i when i > 0 -> Some((qualified.Substring(0, i)).Split('.') |> Array.last)
        | _ -> None
    let s = sigLine.Trim()
    let lines =
        match languageOf path, ownerLeaf with
        | "csharp", Some o -> [| $"public class {o}"; "{"; "    " + s; "}" |]
        | ("typescript" | "javascript"), Some o when not (tsTest.IsMatch s) -> [| $"export class {o} {{"; "  " + s; "}" |]
        | "fsharp", Some o -> [| $"type {o}() ="; "    " + s |]
        | _ -> [| s |]
    extract path lines
    |> List.filter (fun x -> Some x.Name <> ownerLeaf)
    |> List.tryLast
    |> Option.map (fun x -> (x.Name.Split('@')[0]).Split('.') |> Array.last)
