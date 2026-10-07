/// Reflection-driven JSON codec and JSON Schema emitter for the F# model.
/// Both are derived from the same type walk, so the wire format and the schema cannot disagree.
///   record          -> object, camelCase properties; None fields are omitted
///   union, no data  -> string enum ("draft")
///   union with data -> object tagged by "kind" ({"kind":"alter","symbol":...})
///   list / array    -> array
module StoryPlan.Json

open System
open System.Collections.Generic
open System.ComponentModel
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open Microsoft.FSharp.Reflection

let camel (s: string) =
    if String.IsNullOrEmpty s || Char.IsLower s[0] then s
    else string (Char.ToLowerInvariant s[0]) + s.Substring 1

let private isOption (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>

let private isList (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>

let private isEnumUnion (t: Type) =
    FSharpType.IsUnion t && FSharpType.GetUnionCases t |> Array.forall (fun c -> c.GetFields().Length = 0)

let private description (m: MemberInfo) =
    match m.GetCustomAttribute<DescriptionAttribute>() with
    | null -> None
    | d -> Some d.Description

// ---------- encode ----------

let rec encode (t: Type) (v: obj) : JsonNode =
    if t = typeof<string> then JsonValue.Create(v :?> string)
    elif t = typeof<int> then JsonValue.Create(v :?> int)
    elif t = typeof<bool> then JsonValue.Create(v :?> bool)
    elif t = typeof<float> then JsonValue.Create(v :?> float)
    elif isOption t then
        match v with
        | null -> null
        | _ -> encode (t.GetGenericArguments()[0]) (t.GetProperty("Value").GetValue v)
    elif isList t || t.IsArray then
        let et = if t.IsArray then t.GetElementType() else t.GetGenericArguments()[0]
        let arr = JsonArray()
        for x in (v :?> Collections.IEnumerable) do
            arr.Add(encode et x)
        arr
    elif FSharpType.IsRecord t then
        let o = JsonObject()
        for f in FSharpType.GetRecordFields t do
            let n = encode f.PropertyType (f.GetValue v)
            if not (isNull n) then o[camel f.Name] <- n
        o
    elif isEnumUnion t then
        let c, _ = FSharpValue.GetUnionFields(v, t)
        JsonValue.Create(camel c.Name)
    elif FSharpType.IsUnion t then
        let c, values = FSharpValue.GetUnionFields(v, t)
        let o = JsonObject()
        o["kind"] <- JsonValue.Create(camel c.Name)
        for f, x in Array.zip (c.GetFields()) values do
            let n = encode f.PropertyType x
            if not (isNull n) then o[camel f.Name] <- n
        o
    else
        failwithf "Json.encode: unsupported type %s" t.FullName

let toNode<'T> (v: 'T) = encode typeof<'T> (box v)

let private writeOpts = JsonSerializerOptions(WriteIndented = true)
let private compactOpts = JsonSerializerOptions(WriteIndented = false)

let serialize<'T> (v: 'T) = (toNode v).ToJsonString writeOpts
let serializeCompact<'T> (v: 'T) = (toNode v).ToJsonString compactOpts

// ---------- decode ----------

exception DecodeError of path: string * message: string

let private prop (o: JsonObject) (key: string) : JsonNode =
    let mutable n: JsonNode = null
    if o.TryGetPropertyValue(key, &n) then n else null

let private fail path msg = raise (DecodeError(path, msg))

let private kindName (n: JsonNode) =
    match n with
    | :? JsonValue as v ->
        match v.GetValueKind() with
        | JsonValueKind.String -> "string"
        | JsonValueKind.Number -> "number"
        | JsonValueKind.True | JsonValueKind.False -> "boolean"
        | k -> string k
    | :? JsonArray -> "array"
    | :? JsonObject -> "object"
    | _ -> "null"

let rec decode (t: Type) (path: string) (n: JsonNode) : obj =
    let expect what = fail path $"expected {what}, got {kindName n}"
    if isOption t then
        let some = FSharpType.GetUnionCases(t) |> Array.find (fun c -> c.Name = "Some")
        if isNull n then null
        else FSharpValue.MakeUnion(some, [| decode (t.GetGenericArguments()[0]) path n |])
    elif isNull n then fail path "required"
    elif t = typeof<string> then
        match n with
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> box (v.GetValue<string>())
        | _ -> expect "string"
    elif t = typeof<int> then
        match n with
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.Number -> box (v.GetValue<int>())
        | _ -> expect "integer"
    elif t = typeof<bool> then
        match n with
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.True || v.GetValueKind() = JsonValueKind.False -> box (v.GetValue<bool>())
        | _ -> expect "boolean"
    elif t = typeof<float> then
        match n with
        | :? JsonValue as v when v.GetValueKind() = JsonValueKind.Number -> box (v.GetValue<float>())
        | _ -> expect "number"
    elif isList t then
        let et = t.GetGenericArguments()[0]
        match n with
        | :? JsonArray as a ->
            let items = a |> Seq.mapi (fun i x -> decode et $"{path}[{i}]" x) |> Array.ofSeq
            let empty = t.GetProperty("Empty").GetValue null
            let cons = t.GetMethod "Cons"
            Array.foldBack (fun x acc -> cons.Invoke(null, [| x; acc |])) items empty
        | _ -> expect "array"
    elif FSharpType.IsRecord t then
        match n with
        | :? JsonObject as o ->
            let values =
                FSharpType.GetRecordFields t
                |> Array.map (fun f ->
                    let key = camel f.Name
                    let child = prop o key
                    if isNull child && isList f.PropertyType then decode f.PropertyType $"{path}.{key}" (JsonArray())
                    else decode f.PropertyType $"{path}.{key}" child)
            FSharpValue.MakeRecord(t, values)
        | _ -> expect "object"
    elif FSharpType.IsUnion t then
        let cases = FSharpType.GetUnionCases t
        let names = String.Join(", ", cases |> Array.map (fun c -> camel c.Name))
        let tag, body =
            match n with
            | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>(), None
            | :? JsonObject as o ->
                match prop o "kind" with
                | k when not (isNull k) && k.GetValueKind() = JsonValueKind.String -> k.GetValue<string>(), Some o
                | _ -> fail $"{path}.kind" $"required, one of: {names}"
            | _ -> expect $"one of: {names}"
        match cases |> Array.tryFind (fun c -> String.Equals(camel c.Name, tag, StringComparison.OrdinalIgnoreCase)) with
        | None -> fail path $"unknown kind '{tag}', expected one of: {names}"
        | Some c ->
            let values =
                c.GetFields()
                |> Array.map (fun f ->
                    let key = camel f.Name
                    let child =
                        match body with
                        | Some o -> prop o key
                        | None -> null
                    if isNull child && isList f.PropertyType then decode f.PropertyType $"{path}.{key}" (JsonArray())
                    else decode f.PropertyType $"{path}.{key}" child)
            FSharpValue.MakeUnion(c, values)
    else
        failwithf "Json.decode: unsupported type %s" t.FullName

let ofNode<'T> (n: JsonNode) : Result<'T, string> =
    try Result.Ok(decode typeof<'T> "$" n :?> 'T)
    with DecodeError(p, m) -> Result.Error $"{p}: {m}"

let deserialize<'T> (json: string) =
    try ofNode<'T> (JsonNode.Parse json)
    with :? JsonException as e -> Result.Error $"invalid JSON: {e.Message}"

// ---------- schema ----------

/// Emits JSON Schema (2020-12). Named records/unions go to $defs so recursive types stay finite.
type SchemaBuilder() =
    let defs = Dictionary<string, JsonNode>()

    member this.Of(t: Type) : JsonNode =
        let prim (ty: string) = JsonObject(dict [ "type", JsonValue.Create ty :> JsonNode ]) :> JsonNode
        if t = typeof<string> then prim "string"
        elif t = typeof<int> then prim "integer"
        elif t = typeof<bool> then prim "boolean"
        elif t = typeof<float> then prim "number"
        elif isOption t then this.Of(t.GetGenericArguments()[0])
        elif isList t || t.IsArray then
            let et = if t.IsArray then t.GetElementType() else t.GetGenericArguments()[0]
            JsonObject(dict [ "type", JsonValue.Create "array" :> JsonNode; "items", this.Of et ])
        elif FSharpType.IsRecord t || FSharpType.IsUnion t then
            if not (defs.ContainsKey t.Name) then
                defs[t.Name] <- JsonObject() // placeholder breaks recursion
                defs[t.Name] <- this.Define t
            JsonObject(dict [ "$ref", JsonValue.Create $"#/$defs/{t.Name}" :> JsonNode ])
        else
            failwithf "Json.schema: unsupported type %s" t.FullName

    member private this.Object (fields: (string * Type * string option) seq) (tag: string option) (desc: string option) : JsonNode =
        let props = JsonObject()
        let required = JsonArray()
        match tag with
        | Some k ->
            props["kind"] <- JsonObject(dict [ "const", JsonValue.Create k :> JsonNode ])
            required.Add(JsonValue.Create "kind")
        | None -> ()
        for name, ft, d in fields do
            let s = this.Of ft
            d |> Option.iter (fun text -> s["description"] <- JsonValue.Create text)
            props[camel name] <- s
            if not (isOption ft) then required.Add(JsonValue.Create(camel name))
        let o = JsonObject()
        o["type"] <- JsonValue.Create "object"
        desc |> Option.iter (fun d -> o["description"] <- JsonValue.Create d)
        o["properties"] <- props
        o["required"] <- required
        o["additionalProperties"] <- JsonValue.Create false
        o

    member private this.Define(t: Type) : JsonNode =
        let desc = description t
        if FSharpType.IsRecord t then
            let fields = FSharpType.GetRecordFields t |> Seq.map (fun f -> f.Name, f.PropertyType, description f)
            this.Object fields None desc
        elif isEnumUnion t then
            let o = JsonObject()
            o["type"] <- JsonValue.Create "string"
            desc |> Option.iter (fun d -> o["description"] <- JsonValue.Create d)
            o["enum"] <- JsonArray(FSharpType.GetUnionCases t |> Array.map (fun c -> JsonValue.Create(camel c.Name) :> JsonNode))
            o
        else
            let variants =
                FSharpType.GetUnionCases t
                |> Array.map (fun c ->
                    let fields = c.GetFields() |> Seq.map (fun f -> f.Name, f.PropertyType, description f)
                    this.Object fields (Some(camel c.Name)) None)
            let o = JsonObject()
            desc |> Option.iter (fun d -> o["description"] <- JsonValue.Create d)
            o["oneOf"] <- JsonArray(variants)
            o

    member _.Defs =
        let o = JsonObject()
        for KeyValue(k, v) in defs |> Seq.sortBy (fun kv -> kv.Key) do
            o[k] <- v.DeepClone()
        o

/// Schema for a single type, with its $defs.
let schemaOf (t: Type) : JsonObject =
    let b = SchemaBuilder()
    let root = b.Of t
    let o = JsonObject()
    o["$schema"] <- JsonValue.Create "https://json-schema.org/draft/2020-12/schema"
    o["$ref"] <- root["$ref"].DeepClone()
    o["$defs"] <- b.Defs
    o

/// Schema for an object of named arguments (an MCP tool's inputSchema).
/// Each arg: name, type, required, description.
let argsSchema (args: (string * Type * bool * string) list) : JsonObject =
    let b = SchemaBuilder()
    let props = JsonObject()
    let required = JsonArray()
    for name, t, req, desc in args do
        let s = b.Of t
        s["description"] <- JsonValue.Create desc
        props[name] <- s
        if req then required.Add(JsonValue.Create name)
    let o = JsonObject()
    o["type"] <- JsonValue.Create "object"
    o["properties"] <- props
    o["required"] <- required
    let defs = b.Defs
    if defs.Count > 0 then o["$defs"] <- defs
    o
