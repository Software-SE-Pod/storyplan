module StoryPlan.Tests.JsonTests

open System.Text.Json.Nodes
open Xunit
open StoryPlan
open StoryPlan.Model

/// Navigate a JsonNode by property names and array indices, returning the string value.
let rec at (n: JsonNode) (path: obj list) : JsonNode =
    match path with
    | [] -> n
    | (:? string as k) :: rest -> at (n[k]) rest
    | (:? int as i) :: rest -> at (n[i]) rest
    | p :: _ -> failwithf "bad path segment %A" p

let str n path = (at n path).GetValue<string>()

let samplePlan =
    { Id = "X-1"; Title = "t"; Why = "w"; Repo = "C:/r"; Sha = "abc"
      Story = "As a user.\nAcceptance criteria:\n- It works."
      Criteria = [ "It works." ]
      Standards = [ "S1" ]
      Changes =
        [ Extend("a.cs", [ { Name = "A.B"; Kind = Member; Sig = "void B()"; Does = "Does b."; Covers = [ 1 ] } ])
          Alter("a.cs#A.C", Some "int C()", "Returns a count.")
          Touch("README.md", "doc") ]
      Loc = 10; Allow = []; Inspected = []; Status = Draft }

[<Fact>]
let ``plans round-trip through the wire format`` () =
    let json = Json.serialize samplePlan
    Assert.Equal(Result.Ok samplePlan, Json.deserialize<Plan> json)

[<Fact>]
let ``unions are kind-tagged, enums are strings and None fields are omitted`` () =
    let n = Json.toNode samplePlan
    Assert.Equal("extend", str n [ "changes"; 0; "kind" ])
    Assert.Equal("Does b.", str n [ "changes"; 0; "syms"; 0; "does" ])
    Assert.Equal("draft", str n [ "status" ])
    Assert.False((at n [ "changes"; 2 ] :?> JsonObject).ContainsKey "newSig")

[<Fact>]
let ``decode errors name the path and the allowed kinds`` () =
    match Json.deserialize<Change> """{"kind":"rename","symbol":"x"}""" with
    | Result.Error e ->
        Assert.Contains("unknown kind 'rename'", e)
        Assert.Contains("newFile, extend, alter", e)
    | Result.Ok _ -> failwith "expected an error"
    match Json.deserialize<Change> """{"kind":"alter"}""" with
    | Result.Error e -> Assert.Equal("$.symbol: required", e)
    | Result.Ok _ -> failwith "expected an error"

[<Fact>]
let ``schema is generated from the types with recursion through defs`` () =
    let s = Json.schemaOf typeof<Change>
    let variants = at s [ "$defs"; "Change"; "oneOf" ] :?> JsonArray
    let kinds = variants |> Seq.map (fun v -> str v [ "properties"; "kind"; "const" ]) |> List.ofSeq
    Assert.Equal<string list>([ "newFile"; "extend"; "alter"; "remove"; "removeFile"; "touch" ], kinds)
    let required = at variants[2] [ "required" ] :?> JsonArray |> Seq.map (fun x -> x.GetValue<string>()) |> List.ofSeq
    Assert.Equal<string list>([ "kind"; "symbol"; "change" ], required)
    Assert.Equal("#/$defs/NewSym", str variants[1] [ "properties"; "syms"; "items"; "$ref" ])
    Assert.Contains("plain-English", str s [ "$defs"; "NewSym"; "properties"; "does"; "description" ])
    Assert.Equal("string", str s [ "$defs"; "Kind"; "type" ])

[<Fact>]
let ``missing lists decode as empty`` () =
    match Json.deserialize<Change> """{"kind":"extend","file":"a.cs"}""" with
    | Result.Ok(Extend("a.cs", [])) -> ()
    | other -> failwithf "unexpected %A" other
