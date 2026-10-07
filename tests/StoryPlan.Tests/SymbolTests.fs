module StoryPlan.Tests.SymbolTests

open Xunit
open StoryPlan
open StoryPlan.Model
open StoryPlan.Symbols

let names path (text: string) = extract path (splitLines text) |> List.map (fun s -> s.Name)

[<Fact>]
let ``C# members are qualified by their enclosing type, including nested types and tests`` () =
    let text = """namespace X;
public class Outer
{
    private sealed record Props(int A);
    private enum Kind { A, B }

    /// <summary>doc</summary>
    public int Run(string s)
    {
        return 1;
    }

    [Fact]
    public async Task Does_thing_when_x()
    {
    }

    public sealed class Inner
    {
        public void Go() { }
    }
}
"""
    Assert.Equal<string list>(
        [ "Outer"; "Outer.Props"; "Outer.Kind"; "Outer.Run"; "Outer.Does_thing_when_x"; "Outer.Inner"; "Outer.Inner.Go" ],
        names "a.cs" text)
    let run = extract "a.cs" (splitLines text) |> List.find (fun s -> s.Name = "Outer.Run")
    Assert.Equal(7, run.Start)
    Assert.Equal(8, run.Decl)
    Assert.Equal("public int Run(string s)", run.Sig)

[<Fact>]
let ``F# keeps module-level lets and drops function-local ones`` () =
    let text = """module M

let top x =
    let local = x + 1
    local

module Inner =
    let nested = 1
    let f () =
        let deep = 2
        deep

type T() =
    member this.Go() = 1
"""
    Assert.Equal<string list>([ "M"; "top"; "Inner"; "nested"; "f"; "T"; "T.Go" ], names "a.fs" text)

[<Fact>]
let ``TypeScript exports and test cases are symbols`` () =
    let text = """export interface Props { a: string }
export async function mount(el: Element) {}
const hidden = 1;
export const VERSION = "1";
it('mounts once', () => {});
"""
    Assert.Equal<string list>([ "Props"; "mount"; "hidden"; "VERSION"; "mounts_once" ], names "a.ts" text)

[<Fact>]
let ``TypeScript class and interface members are qualified, control flow is not`` () =
    let text = """export class Island {
  readonly id: string;
  private count = 0;
  async start(): Promise<void> {
    if (this.count) {
      return;
    }
    for (const x of []) { }
  }
  #stop(): void { }
}
interface Stats {
  errors: number;
  pageUpdates?: number;
}
describe('runtime', () => {
  it('counts load failures', async () => { });
});
"""
    Assert.Equal<string list>(
        [ "Island"; "Island.id"; "Island.start"; "Island.#stop"; "Stats"; "Stats.errors"; "Stats.pageUpdates"; "counts_load_failures" ],
        names "a.ts" text)

[<Theory>]
[<InlineData("a.cs", "Orders.Refund", "public bool Refund(int id)", "Refund")>]
[<InlineData("a.cs", "OrdersTests.Refund_works", "public void Refund_works()", "Refund_works")>]
[<InlineData("a.ts", "does_x_when_y", "it('does x when y', async () => {", "does_x_when_y")>]
[<InlineData("a.ts", "Island.start", "async start(): Promise<void> {", "start")>]
[<InlineData("a.fs", "T.Go", "member this.Go() =", "Go")>]
let ``names are derived from signatures the way the index will see them`` (path: string, name: string, sigLine: string, expected: string) =
    Assert.Equal(Some expected, nameFromSig path name sigLine)

[<Fact>]
let ``overloads get unique handles`` () =
    let text = "public class A\n{\n    public void F(int x) { }\n    public void F(string s) { }\n}\n"
    Assert.Equal<string list>([ "A"; "A.F"; "A.F@4" ], names "a.cs" text)

[<Fact>]
let ``a line belongs to the innermost symbol that starts before it`` () =
    let syms = extract "a.cs" (splitLines "public class A\n{\n    /// doc\n    public void F()\n    {\n    }\n}\n")
    Assert.Equal(Some "A", owner syms 2 |> Option.map (fun s -> s.Name))
    Assert.Equal(Some "A.F", owner syms 3 |> Option.map (fun s -> s.Name))
    Assert.Equal(Some "A.F", owner syms 6 |> Option.map (fun s -> s.Name))
