/// The repo index: every file and symbol at one commit, plus an identifier -> files map that powers
/// reference counts, blast radius and repo-map ranking without a language server.
module StoryPlan.Index

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.RegularExpressions
open StoryPlan.Model
open StoryPlan.Symbols

let indexedExtensions =
    set [ ".cs"; ".fs"; ".fsx"; ".fsi"; ".ts"; ".tsx"; ".mts"; ".js"; ".jsx"; ".mjs"; ".cjs"; ".py"; ".go"; ".java"; ".rs"
          ".razor"; ".cshtml"; ".md"; ".json"; ".yml"; ".yaml"; ".props"; ".targets"; ".csproj"; ".fsproj"; ".sln"; ".slnx"; ".css"; ".html" ]

let private skip (p: string) =
    p.EndsWith "package-lock.json" || p.EndsWith "pnpm-lock.yaml" || p.EndsWith "yarn.lock" || p.Contains "/node_modules/"

type RepoIndex =
    { Root: string
      Sha: string
      Files: string[]
      Lines: IReadOnlyDictionary<string, string[]>
      Syms: IReadOnlyDictionary<string, Sym list>
      ByHandle: IReadOnlyDictionary<string, Sym>
      /// identifier -> files that mention it
      Words: IReadOnlyDictionary<string, HashSet<string>>
      Backends: string list }

/// A backend that can replace regex symbols for a file (SCIP). Returns None to fall back.
type SymbolOverride = string -> string[] -> Sym list option

let private word = Regex(@"[A-Za-z_][A-Za-z0-9_]*")

let build (root: string) (sha: string) (overrides: SymbolOverride list) : RepoIndex =
    let files =
        Git.lsTree root sha
        |> Array.filter (fun p -> indexedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()) && not (skip p))
    let blobs = Git.readBlobs root sha files
    let lines = Dictionary<string, string[]>()
    let syms = Dictionary<string, Sym list>()
    let byHandle = Dictionary<string, Sym>()
    let words = Dictionary<string, HashSet<string>>()
    let backends = HashSet<string>()
    for f in files do
        let text = blobs |> Map.tryFind f |> Option.defaultValue ""
        if text.Length < 1_000_000 then
            let ls = splitLines text
            lines[f] <- ls
            let s =
                overrides
                |> List.tryPick (fun o -> o f ls)
                |> Option.defaultWith (fun () -> extract f ls)
            for x in s do
                byHandle[x.Handle] <- x
                backends.Add x.Backend |> ignore
            syms[f] <- s
            for m in word.Matches text do
                match words.TryGetValue m.Value with
                | true, set -> set.Add f |> ignore
                | _ -> words[m.Value] <- HashSet [ f ]
    { Root = root; Sha = sha; Files = files; Lines = lines; Syms = syms; ByHandle = byHandle; Words = words
      Backends = List.ofSeq backends }

let private cache = ConcurrentDictionary<string, Lazy<RepoIndex>>()

/// Index for (root, rev), cached by resolved commit plus a salt that changes when symbol sources do.
let get (root: string) (rev: string) (overrides: SymbolOverride list) (salt: string) =
    let root = Path.GetFullPath root
    let sha = Git.revParse root rev
    cache.GetOrAdd($"{root}|{sha}|{salt}", fun _ -> lazy (build root sha overrides)).Value

let leaf (name: string) =
    let n = (name.Split('@')[0]).Split('.')
    n[n.Length - 1]

/// Files (other than the symbol's own) that reference the symbol: exact from SCIP, else by its leaf name.
let references (ix: RepoIndex) (s: Sym) =
    match s.Refs with
    | Some files -> files |> List.filter ((<>) s.File)
    | None ->
        match ix.Words.TryGetValue(leaf s.Name) with
        | true, files -> files |> Seq.filter ((<>) s.File) |> Seq.sort |> List.ofSeq
        | _ -> []

let symbolsOf (ix: RepoIndex) path =
    match ix.Syms.TryGetValue path with
    | true, s -> s
    | _ -> []

let fileExists (ix: RepoIndex) path = ix.Lines.ContainsKey path || Array.contains path ix.Files

let dirExists (ix: RepoIndex) (path: string) =
    let dir = match path.LastIndexOf '/' with | -1 -> "" | i -> path.Substring(0, i + 1)
    dir = "" || ix.Files |> Array.exists (fun f -> f.StartsWith dir)

// ---------- search ----------

let private distance (a: string) (b: string) =
    let a, b = a.ToLowerInvariant(), b.ToLowerInvariant()
    let d = Array2D.zeroCreate (a.Length + 1) (b.Length + 1)
    for i in 0 .. a.Length do d[i, 0] <- i
    for j in 0 .. b.Length do d[0, j] <- j
    for i in 1 .. a.Length do
        for j in 1 .. b.Length do
            let cost = if a[i - 1] = b[j - 1] then 0 else 1
            d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)
    d[a.Length, b.Length]

/// Ranked symbols for a free-text query: exact leaf, then prefix, then substring, then edit distance.
let find (ix: RepoIndex) (query: string) (limit: int) =
    let q = query.Trim()
    let qHandle = Handle.split q
    let score (s: Sym) =
        let l = leaf s.Name
        match qHandle with
        | Some(p, n) when s.File = p && s.Name = n -> 0
        | Some(_, n) when s.Name = n -> 1
        | _ when s.Name = q || l = q -> 1
        | _ when s.Name.EndsWith("." + q) -> 2
        | _ when l.StartsWith(q, StringComparison.OrdinalIgnoreCase) -> 3
        | _ when s.Handle.Contains(q, StringComparison.OrdinalIgnoreCase) -> 4
        | _ ->
            let target = match qHandle with Some(_, n) -> leaf n | None -> q
            let d = distance l target
            if d <= max 2 (target.Length / 3) then 5 + d else 100
    ix.ByHandle.Values
    |> Seq.map (fun s -> score s, s)
    |> Seq.filter (fun (sc, _) -> sc < 100)
    |> Seq.sortBy (fun (sc, s) -> sc, s.Handle.Length)
    |> Seq.truncate limit
    |> Seq.map snd
    |> List.ofSeq

let resolve (ix: RepoIndex) (handle: string) =
    match ix.ByHandle.TryGetValue handle with
    | true, s -> Result.Ok s
    | _ ->
        let near = find ix handle 3 |> List.map (fun s -> s.Handle)
        let hint = if near.IsEmpty then "use find_symbol" else "did you mean: " + String.Join(", ", near)
        Result.Error $"unknown symbol '{handle}' at {ix.Sha.Substring(0, 7)}; {hint}"

// ---------- repo map ----------

let private estimateTokens (s: string) = s.Length / 4

/// Signatures-only view, files ranked by how often other files reference their symbols, cut to a token budget.
let repoMap (ix: RepoIndex) (under: string) (budget: int) =
    let rank (f: string) =
        let weight = if f.Contains "test" || f.Contains "spec" || f.Contains "sample" then 0.3 else 1.0
        let refs =
            symbolsOf ix f
            |> List.filter (fun s -> (s.Kind = Type || s.Kind = Func) && (leaf s.Name).Length >= 4)
            |> List.sumBy (fun s -> (references ix s).Length)
        float refs * weight
    let candidates =
        ix.Files
        |> Array.filter (fun f -> under = "" || f.StartsWith under)
        |> Array.sortByDescending (fun f -> rank f, -f.Length)
    let sb = StringBuilder()
    let backends = String.Join("+", ix.Backends |> List.sort)
    sb.AppendLine($"# repo map @ {ix.Sha.Substring(0, 7)} ({backends}). Handle = <path>#<name>. Lines: <name> | <signature>") |> ignore
    let mutable shown = 0
    let mutable stop = false
    for f in candidates do
        if not stop then
            let block = StringBuilder()
            block.AppendLine f |> ignore
            for s in symbolsOf ix f do
                block.AppendLine($"  {s.Name} | {s.Sig}") |> ignore
            if estimateTokens (sb.ToString() + block.ToString()) > budget && shown > 0 then stop <- true
            else
                sb.Append(block) |> ignore
                shown <- shown + 1
    if shown < candidates.Length then
        sb.AppendLine($"... {candidates.Length - shown} more files; narrow with `under` or raise `budget`") |> ignore
    sb.ToString()

/// Body of a symbol: from its first lead line to the line before the next symbol (or its End).
let body (ix: RepoIndex) (s: Sym) =
    let ls = ix.Lines[s.File]
    let next =
        symbolsOf ix s.File
        |> List.filter (fun x -> x.Start > s.Decl)
        |> List.tryHead
        |> Option.map (fun x -> x.Start - 1)
        |> Option.defaultValue ls.Length
    let last = if s.End > 0 then s.End else next
    let declIndent = ls[s.Decl - 1].Length - ls[s.Decl - 1].TrimStart().Length
    let mutable stop = last
    // Without an end line, the span runs to the next symbol; drop closing braces that belong to the parent.
    while s.End = 0 && stop > s.Decl
          && (let l = ls[stop - 1] in l.Trim() = "" || (l.Trim().StartsWith "}" && l.Length - l.TrimStart().Length < declIndent)) do
        stop <- stop - 1
    String.Join("\n", ls[s.Start - 1 .. max (s.Start - 1) (stop - 1)]).TrimEnd()
