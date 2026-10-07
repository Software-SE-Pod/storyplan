module StoryPlan.Tests.ScipTests

open System.IO
open System.Text
open Xunit
open StoryPlan
open StoryPlan.Model

// Minimal protobuf writer, enough to build SCIP fixtures by hand.
let varint (v: int) =
    let out = ResizeArray<byte>()
    let mutable x = uint32 v
    while x >= 0x80u do
        out.Add(byte (x &&& 0x7Fu ||| 0x80u))
        x <- x >>> 7
    out.Add(byte x)
    out.ToArray()

let tag field wire = varint ((field <<< 3) ||| wire)
let bytesField field (data: byte[]) = Array.concat [ tag field 2; varint data.Length; data ]
let strField field (s: string) = bytesField field (Encoding.UTF8.GetBytes s)
let intField field v = Array.append (tag field 0) (varint v)
let packedField field (xs: int list) = bytesField field (xs |> List.map varint |> Array.concat)

let multiRange (sl, sc, el, ec) = Array.concat [ intField 1 sl; intField 2 sc; intField 3 el; intField 4 ec ]

let occurrence symbol roles (range: byte[]) (enclosing: byte[] option) =
    Array.concat [ strField 2 symbol; intField 3 roles; range; (match enclosing with Some e -> e | None -> [||]) ]

let pkg = "scip-typescript npm shop 1.0.0 src/`cart.ts`/"

let index () =
    let doc =
        Array.concat
            [ strField 1 "src/cart.ts"
              // typed ranges (current SCIP): class Cart spans lines 1-6
              bytesField 2 (occurrence (pkg + "Cart#") 1 (bytesField 9 (multiRange (1, 13, 1, 17))) (Some(bytesField 11 (multiRange (1, 0, 6, 1)))))
              // legacy packed range (older indexers): method add on line 3
              bytesField 2 (occurrence (pkg + "Cart#add().") 1 (packedField 1 [ 3; 2; 5 ]) (Some(packedField 7 [ 3; 2; 5; 3 ])))
              // a reference, not a definition: ignored
              bytesField 2 (occurrence (pkg + "Cart#") 8 (packedField 1 [ 8; 4; 8; 8 ]) None)
              bytesField 2 (occurrence "local 4" 1 (packedField 1 [ 4; 8; 9 ]) None)
              bytesField 2 (occurrence (pkg + "Cart#add().(item)") 1 (packedField 1 [ 3; 6; 10 ]) None)
              bytesField 3 (Array.concat [ strField 1 (pkg + "Cart#"); intField 5 7; bytesField 7 (strField 5 "class Cart") ]) ]
    bytesField 2 doc

let source =
    [| ""
       "export class Cart {"
       "  // adds"
       "  add(item: string) {"
       "    const n = 1;"
       "  }"
       "}"
       ""
       "new Cart();" |]

[<Fact>]
let ``reads definitions from typed and legacy ranges and skips locals, parameters and references`` () =
    let docs = Scip.readIndex (index ())
    let doc = Assert.Single docs
    let syms = Scip.toSyms (fun s -> if s.EndsWith "Cart#" then Some [ "src/cart.ts"; "src/other.ts" ] else None) "" doc source
    Assert.Equal<string list>([ "Cart"; "Cart.add" ], syms |> List.map (fun s -> s.Name))
    let cart = syms[0]
    Assert.Equal(Type, cart.Kind)
    Assert.Equal("class Cart", cart.Sig)
    Assert.Equal(Some [ "src/other.ts" ], cart.Refs)
    Assert.Equal((2, 2, 7), (cart.Start, cart.Decl, cart.End))
    let add = syms[1]
    Assert.Equal(Member, add.Kind)
    Assert.Equal((3, 4, 6), (add.Start, add.Decl, add.End))
    Assert.Equal("scip", add.Backend)

[<Fact>]
let ``descriptors handle backtick-escaped names and method disambiguators`` () =
    Assert.Equal(Some("Weird`Name.run", Member), Scip.qualified "scip-python python pkg 1.0 mod/`Weird``Name`#run(+1).")
    Assert.Equal(Some("helper", Func), Scip.qualified "scip-typescript npm p 1.0 src/`a.ts`/helper().")
    Assert.Equal(None, Scip.qualified "scip-typescript npm p 1.0 src/`a.ts`/helper().(arg)")
    Assert.Equal(None, Scip.qualified "local 12")

[<Fact>]
let ``a repo's scip index replaces regex symbols for the files it covers`` () =
    let repo, _ = Fixtures.newRepo ()
    Fixtures.write repo "src/cart.ts" (System.String.Join("\n", source))
    Fixtures.commit repo "cart" |> ignore
    File.WriteAllBytes(Path.Combine(repo, ".storyplan", "ts.scip"), index ())
    let ix = Engine.indexAt repo "HEAD"
    Assert.True(ix.ByHandle.ContainsKey "src/cart.ts#Cart.add")
    Assert.Equal("scip", ix.ByHandle["src/cart.ts#Cart.add"].Backend)
    Assert.Equal("regex", ix.ByHandle["src/Orders/OrderService.cs#OrderService.Place"].Backend)
