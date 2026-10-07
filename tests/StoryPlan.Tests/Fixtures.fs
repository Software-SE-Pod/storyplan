/// Shared fixtures: an isolated STORYPLAN_HOME and throwaway git repos.
module StoryPlan.Tests.Fixtures

open System
open System.IO
open StoryPlan

[<assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)>]
do ()

let home =
    let h = Path.Combine(Path.GetTempPath(), "storyplan-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory h |> ignore
    Environment.SetEnvironmentVariable("STORYPLAN_HOME", h)
    h

let private ident = [ "-c"; "user.name=StoryPlan Tests"; "-c"; "user.email=tests@example.invalid"; "-c"; "commit.gpgsign=false" ]

let git (repo: string) (args: string list) = Git.run repo (ident @ args) |> ignore

let write (repo: string) (path: string) (text: string) =
    let full = Path.Combine(repo, path)
    Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
    File.WriteAllText(full, text.Replace("\r\n", "\n"))

let commit repo (msg: string) =
    git repo [ "add"; "-A" ]
    git repo [ "commit"; "-q"; "-m"; msg ]
    Git.revParse repo "HEAD"

/// A small C# repo with one service, one test class and a standards file.
let newRepo () =
    let repo = Path.Combine(home, "repo-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory repo |> ignore
    git repo [ "init"; "-q"; "-b"; "main" ]
    write repo "src/Orders/OrderService.cs" """// Licensed under the MIT license.
namespace Shop;

public sealed class OrderService
{
    /// <summary>Places an order.</summary>
    public int Place(string sku, int qty)
    {
        return qty;
    }

    /// <summary>Cancels an order.</summary>
    public void Cancel(int id)
    {
    }
}
"""
    write repo "tests/Shop.Tests/OrderServiceTests.cs" """namespace Shop.Tests;

public class OrderServiceTests
{
    [Fact]
    public void Place_returns_quantity()
    {
        Assert.Equal(2, new OrderService().Place("a", 2));
    }
}
"""
    write repo "README.md" "# Shop\n"
    write repo ".storyplan/standards.json" """[
  { "id": "CS.XmlDoc", "text": "public API has ///", "exemplar": "src/Orders/OrderService.cs:6", "glob": "src/**/*.cs",
    "check": { "kind": "precededBy", "line": "^\\s*public\\s", "prev": "^\\s*///", "skip": "^\\s*\\[" } },
  { "id": "TEST.Names", "text": "facts are sentences", "exemplar": "tests/Shop.Tests/OrderServiceTests.cs:6", "glob": "tests/**/*.cs",
    "check": { "kind": "captureMatches", "line": "^\\s*public\\s+void\\s+(\\w+)\\s*\\(", "capture": "_" } }
]"""
    let sha = commit repo "init"
    repo, sha
