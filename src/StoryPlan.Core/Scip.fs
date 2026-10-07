/// SCIP backend: reads index.scip files (any language with a SCIP indexer: TypeScript, Python, Java, Go,
/// Rust, C#, Ruby...) and turns definitions into the same Sym shape the regex adapters produce.
/// The protobuf is decoded by hand (only the fields we need), so there is no codegen or package dependency.
module StoryPlan.Scip

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open StoryPlan.Model
open StoryPlan.Symbols

// ---------- protobuf wire reader ----------

type private Reader(data: byte[], start: int, finish: int) =
    let mutable pos = start
    member _.Eof = pos >= finish
    member _.Varint() : uint64 =
        let mutable result = 0UL
        let mutable shift = 0
        let mutable go = true
        while go do
            let b = data[pos]
            pos <- pos + 1
            result <- result ||| (uint64 (b &&& 0x7Fuy) <<< shift)
            shift <- shift + 7
            go <- b &&& 0x80uy <> 0uy
        result
    member this.Tag() = let t = this.Varint() in int (t >>> 3), int (t &&& 7UL)
    member this.Bytes() =
        let len = int (this.Varint())
        let s = pos
        pos <- pos + len
        s, s + len
    member this.Skip(wire: int) =
        match wire with
        | 0 -> this.Varint() |> ignore
        | 1 -> pos <- pos + 8
        | 2 -> this.Bytes() |> ignore
        | 5 -> pos <- pos + 4
        | w -> failwithf "unsupported protobuf wire type %d" w
    member _.Data = data

let private str (data: byte[]) (s: int, e: int) = Encoding.UTF8.GetString(data, s, e - s)

let private packed (r: Reader) (wire: int) (acc: ResizeArray<int>) =
    if wire = 2 then
        let s, e = r.Bytes()
        let inner = Reader(r.Data, s, e)
        while not inner.Eof do acc.Add(int (inner.Varint()))
    else acc.Add(int (r.Varint()))

/// [startLine; startChar; endLine; endChar] from either a typed range message or the legacy packed array.
let private typedRange (data: byte[]) (s, e) (multi: bool) =
    let r = Reader(data, s, e)
    let f = Array.zeroCreate 4
    while not r.Eof do
        let field, wire = r.Tag()
        if wire = 0 && field >= 1 && field <= 4 then f[field - 1] <- int (r.Varint()) else r.Skip wire
    if multi then f else [| f[0]; f[1]; f[0]; f[2] |]

let private normalize (legacy: ResizeArray<int>) =
    match legacy.Count with
    | 3 -> Some [| legacy[0]; legacy[1]; legacy[0]; legacy[2] |]
    | 4 -> Some(legacy.ToArray())
    | _ -> None

type Occurrence = { Symbol: string; Roles: int; Range: int[] option; Enclosing: int[] option }
type SymbolInfo = { Symbol: string; Kind: int; DisplayName: string; Signature: string }
type Document = { Path: string; Occurrences: Occurrence list; Symbols: SymbolInfo list }

let private occurrence (data: byte[]) (s, e) =
    let r = Reader(data, s, e)
    let range = ResizeArray()
    let enclosing = ResizeArray()
    let mutable typed = None
    let mutable typedEnclosing = None
    let mutable symbol = ""
    let mutable roles = 0
    while not r.Eof do
        match r.Tag() with
        | 1, w -> packed r w range
        | 2, 2 -> symbol <- str data (r.Bytes())
        | 3, 0 -> roles <- int (r.Varint())
        | 7, w -> packed r w enclosing
        | 8, 2 -> typed <- Some(typedRange data (r.Bytes()) false)
        | 9, 2 -> typed <- Some(typedRange data (r.Bytes()) true)
        | 10, 2 -> typedEnclosing <- Some(typedRange data (r.Bytes()) false)
        | 11, 2 -> typedEnclosing <- Some(typedRange data (r.Bytes()) true)
        | _, w -> r.Skip w
    { Symbol = symbol; Roles = roles
      Range = typed |> Option.orElse (normalize range)
      Enclosing = typedEnclosing |> Option.orElse (normalize enclosing) }

let private signatureText (data: byte[]) (s, e) =
    let r = Reader(data, s, e)
    let mutable text = ""
    while not r.Eof do
        match r.Tag() with
        | 5, 2 -> text <- str data (r.Bytes())
        | _, w -> r.Skip w
    text

let private symbolInfo (data: byte[]) (s, e) =
    let r = Reader(data, s, e)
    let mutable info = { Symbol = ""; Kind = 0; DisplayName = ""; Signature = "" }
    while not r.Eof do
        match r.Tag() with
        | 1, 2 -> info <- { info with Symbol = str data (r.Bytes()) }
        | 5, 0 -> info <- { info with Kind = int (r.Varint()) }
        | 6, 2 -> info <- { info with DisplayName = str data (r.Bytes()) }
        | 7, 2 -> info <- { info with Signature = signatureText data (r.Bytes()) }
        | _, w -> r.Skip w
    info

let private document (data: byte[]) (s, e) =
    let r = Reader(data, s, e)
    let mutable path = ""
    let occ = ResizeArray()
    let syms = ResizeArray()
    while not r.Eof do
        match r.Tag() with
        | 1, 2 -> path <- str data (r.Bytes())
        | 2, 2 -> occ.Add(occurrence data (r.Bytes()))
        | 3, 2 -> syms.Add(symbolInfo data (r.Bytes()))
        | _, w -> r.Skip w
    { Path = path.Replace('\\', '/'); Occurrences = List.ofSeq occ; Symbols = List.ofSeq syms }

let readIndex (bytes: byte[]) : Document list =
    let r = Reader(bytes, 0, bytes.Length)
    [ while not r.Eof do
          match r.Tag() with
          | 2, 2 -> yield document bytes (r.Bytes())
          | _, w -> r.Skip w ]

// ---------- SCIP symbol strings ----------

type Descriptor =
    | Namespace of string
    | TypeD of string
    | Term of string
    | Method of string
    | Other of string

/// Parses the descriptor suffix of a SCIP symbol ("scheme manager package version descriptors").
let descriptors (symbol: string) : Descriptor list option =
    if symbol.StartsWith "local " then None
    else
        // Skip the four space-separated header fields; "  " inside them is an escaped space.
        let mutable i = 0
        let mutable spaces = 0
        while i < symbol.Length && spaces < 4 do
            if symbol[i] = ' ' then
                if i + 1 < symbol.Length && symbol[i + 1] = ' ' then i <- i + 1 else spaces <- spaces + 1
            i <- i + 1
        let s = symbol.Substring(min i symbol.Length)
        let out = ResizeArray()
        let mutable p = 0
        let readName () =
            if p < s.Length && s[p] = '`' then
                let sb = StringBuilder()
                p <- p + 1
                let mutable go = true
                while go && p < s.Length do
                    if s[p] = '`' && p + 1 < s.Length && s[p + 1] = '`' then sb.Append('`') |> ignore; p <- p + 2
                    elif s[p] = '`' then p <- p + 1; go <- false
                    else sb.Append(s[p]) |> ignore; p <- p + 1
                sb.ToString()
            else
                let st = p
                while p < s.Length && (Char.IsLetterOrDigit s[p] || s[p] = '_' || s[p] = '+' || s[p] = '-' || s[p] = '$') do p <- p + 1
                s.Substring(st, p - st)
        let mutable ok = true
        while ok && p < s.Length do
            match s[p] with
            | '(' ->
                // parameter: (name)
                let close = s.IndexOf(')', p)
                out.Add(Other(s.Substring(p + 1, max 0 (close - p - 1))))
                p <- close + 1
            | '[' ->
                let close = s.IndexOf(']', p)
                out.Add(Other(s.Substring(p + 1, max 0 (close - p - 1))))
                p <- close + 1
            | _ ->
                let name = readName ()
                if p >= s.Length then ok <- false
                else
                    match s[p] with
                    | '/' -> out.Add(Namespace name); p <- p + 1
                    | '#' -> out.Add(TypeD name); p <- p + 1
                    | '.' -> out.Add(Term name); p <- p + 1
                    | ':' | '!' -> out.Add(Other name); p <- p + 1
                    | '(' ->
                        let close = s.IndexOf(')', p)
                        out.Add(Method name)
                        p <- close + 1
                        if p < s.Length && s[p] = '.' then p <- p + 1
                    | _ -> ok <- false
        if ok then Some(List.ofSeq out) else None

/// "Type.Member" style name and kind, or None for locals, parameters and type parameters.
let qualified (symbol: string) : (string * Kind) option =
    match descriptors symbol with
    | None -> None
    | Some ds ->
        match List.tryLast ds with
        | None | Some(Other _) | Some(Namespace _) -> None
        | Some last ->
            let parts = ds |> List.choose (function TypeD n | Term n | Method n -> Some n | _ -> None)
            if parts.IsEmpty || ds |> List.exists (function Other _ -> true | _ -> false) then None
            else
                let underType = ds.Length >= 2 && (match ds[ds.Length - 2] with TypeD _ -> true | _ -> false)
                let kind =
                    match last with
                    | TypeD _ -> Type
                    | Method _ -> if underType then Member else Func
                    | _ -> if underType then Member else Value
                Some(String.Join(".", parts), kind)

let private kindOf (scipKind: int) (fallback: Kind) =
    match scipKind with
    | 7 | 11 | 21 | 49 | 54 | 55 | 53 | 59 | 29 | 30 -> Type
    | 26 | 9 | 41 | 15 | 18 | 45 | 66 | 72 | 80 | 81 | 79 | 69 | 70 | 68 -> Member
    | 17 -> Func
    | 8 | 61 | 60 -> Value
    | _ -> fallback

// ---------- documents -> Sym overrides ----------

/// refsOf maps a SCIP symbol string to the files that reference it (None when unknown).
let toSyms (refsOf: string -> string list option) (pathPrefix: string) (doc: Document) (lines: string[]) : Sym list =
    let path = pathPrefix + doc.Path
    let defs =
        doc.Occurrences
        |> List.filter (fun o -> o.Roles &&& 1 = 1 && o.Range.IsSome)
        |> List.map (fun o -> o.Symbol, o)
        |> dict
    let infos = doc.Symbols |> List.map (fun s -> s.Symbol, s) |> dict
    let all =
        [ for KeyValue(symbol, occ) in defs do
              match qualified symbol with
              | None -> ()
              | Some(name, fallbackKind) ->
                  let line = occ.Range.Value[0] + 1
                  if line >= 1 && line <= lines.Length then
                      let info = match infos.TryGetValue symbol with | true, i -> Some i | _ -> None
                      let mutable start = line - 1
                      while start > 0 && (let t = lines[start - 1].TrimStart() in t.StartsWith "//" || t.StartsWith "/*" || t.StartsWith "*" || t.StartsWith "@" || t.StartsWith "[" || t.StartsWith "#") do
                          start <- start - 1
                      let sigText =
                          match info with
                          | Some i when i.Signature <> "" -> (i.Signature.Split('\n')[0]).Trim()
                          | _ -> signature lines[line - 1]
                      { Handle = $"{path}#{name}"; File = path; Name = name
                        Kind = kindOf (info |> Option.map (fun i -> i.Kind) |> Option.defaultValue 0) fallbackKind
                        Sig = sigText; Start = start + 1; Decl = line
                        End = (match occ.Enclosing with Some r -> r[2] + 1 | None -> 0)
                        Backend = "scip"
                        Refs = refsOf symbol |> Option.map (List.filter ((<>) path)) } ]
        |> List.sortBy (fun s -> s.Decl, s.Name.Length)
    let seen = Collections.Generic.HashSet<string>()
    all |> List.filter (fun s -> seen.Add s.Name)

/// Where SCIP indexes for a repo live: <repo>/.storyplan/*.scip and $STORYPLAN_HOME/scip/<repo-folder>/*.scip.
/// A file named "<prefix>@@name.scip" maps its relative paths under <prefix> (with '~' for '/'), e.g.
/// "src~client~@@ts.scip" for an index whose paths are relative to src/client.
let discover (root: string) =
    let dirs =
        [ Path.Combine(root, ".storyplan")
          Path.Combine(Standards.home (), "scip", Path.GetFileName(Path.TrimEndingDirectorySeparator root)) ]
    [ for d in dirs do
          if Directory.Exists d then yield! Directory.GetFiles(d, "*.scip") ]

let private prefixOf (file: string) =
    let name = Path.GetFileNameWithoutExtension file
    match name.IndexOf "@@" with
    | i when i > 0 -> name.Substring(0, i).Replace('~', '/') + "/"
    | _ -> ""

let private loaded = ConcurrentDictionary<string, DateTime * (string * Document) list>()

let private load (file: string) =
    let stamp = File.GetLastWriteTimeUtc file
    match loaded.TryGetValue file with
    | true, (t, docs) when t = stamp -> docs
    | _ ->
        let prefix = prefixOf file
        let docs = readIndex (File.ReadAllBytes file) |> List.map (fun d -> prefix, d)
        loaded[file] <- (stamp, docs)
        docs

/// A per-file override for Index.build. Falls back to regex when the index doesn't cover the file or its
/// definitions don't line up with the file content at this commit (a stale index).
let overridesFor (root: string) : (string -> string[] -> Sym list option) list =
    match discover root with
    | [] -> []
    | files ->
        let all = files |> List.collect load
        let docs = all |> List.map (fun (prefix, d) -> prefix + d.Path, (prefix, d)) |> dict
        // Exact references: every non-definition occurrence of a symbol, by file.
        let refs = Collections.Generic.Dictionary<string, Collections.Generic.HashSet<string>>()
        for prefix, d in all do
            for o in d.Occurrences do
                if o.Roles &&& 1 = 0 && not (o.Symbol.StartsWith "local ") then
                    match refs.TryGetValue o.Symbol with
                    | true, set -> set.Add(prefix + d.Path) |> ignore
                    | _ -> refs[o.Symbol] <- Collections.Generic.HashSet [ prefix + d.Path ]
        let refsOf symbol =
            match refs.TryGetValue symbol with
            | true, set -> Some(set |> Seq.sort |> List.ofSeq)
            | _ -> Some []
        [ fun path lines ->
              match docs.TryGetValue path with
              | true, (prefix, d) ->
                  let syms = toSyms refsOf prefix d lines |> List.filter (fun s -> not ((Index.leaf s.Name).StartsWith "<"))
                  let matches = syms |> List.filter (fun s -> lines[s.Decl - 1].Contains(Index.leaf s.Name)) |> List.length
                  if not syms.IsEmpty && float matches >= 0.8 * float syms.Length then
                      // SCIP only knows definitions; keep regex-only symbols such as test cases (it('...'), [Fact]).
                      let names = syms |> List.map (fun s -> s.Name) |> set
                      let extra = extract path lines |> List.filter (fun s -> not (names.Contains s.Name))
                      Some(syms @ extra |> List.sortBy (fun s -> s.Decl))
                  else None
              | _ -> None ]
