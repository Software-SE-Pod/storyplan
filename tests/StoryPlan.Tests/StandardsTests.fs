module StoryPlan.Tests.StandardsTests

open Xunit
open StoryPlan
open StoryPlan.Model

let rule check = { Id = "R"; Text = ""; Exemplar = ""; Glob = "src/**/*.cs"; Check = Some check }
let lines (s: string) = s.Replace("\r\n", "\n").Split('\n')

[<Fact>]
let ``forbid flags changed lines only, honoring unless`` () =
    let text = lines "a = Deserialize(x);\nb = Deserialize(x, IslandJson.Default);\nc = Deserialize(y);"
    let r = rule (Forbid(@"\bDeserialize\b", Some "IslandJson"))
    Assert.Equal<string list>([ "f.cs:1 a = Deserialize(x);" ], Standards.evaluate r "f.cs" text [ 1; 2 ])

[<Fact>]
let ``precededBy skips attributes before checking the previous line`` () =
    let text = lines "/// doc\n[Obsolete]\npublic void A() {}\n\npublic void B() {}"
    let r = rule (PrecededBy(@"^\s*public\s", @"^\s*///", Some @"^\s*\["))
    Assert.Equal<string list>([ "f.cs:5 public void B() {}" ], Standards.evaluate r "f.cs" text [ 3; 5 ])

[<Fact>]
let ``firstLine only applies when line 1 changed`` () =
    let r = rule (FirstLine "// header")
    Assert.Empty(Standards.evaluate r "f.cs" (lines "x") [ 2 ])
    Assert.Single(Standards.evaluate r "f.cs" (lines "x") [ 1 ]) |> ignore

[<Fact>]
let ``captureMatches checks group 1`` () =
    let r = rule (CaptureMatches(@"void (\w+)\(", "_"))
    Assert.Equal<string list>([ "f.cs:2 'Bad' does not match /_/" ], Standards.evaluate r "f.cs" (lines "void Good_one()\nvoid Bad()") [ 1; 2 ])

[<Theory>]
[<InlineData("src/**/*.cs", "src/a/b/C.cs", true)>]
[<InlineData("src/**/*.cs", "src/C.cs", true)>]
[<InlineData("src/**/*.cs", "tests/C.cs", false)>]
[<InlineData("*.md", "README.md", true)>]
[<InlineData("*.md", "docs/README.md", false)>]
let ``globs`` (glob: string, path: string, expected: bool) = Assert.Equal(expected, Standards.globMatch glob path)
