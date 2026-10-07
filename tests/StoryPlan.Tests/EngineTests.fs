module StoryPlan.Tests.EngineTests

open System.IO
open System.Text.Json.Nodes
open Xunit
open StoryPlan
open StoryPlan.Model
open StoryPlan.Tests.Fixtures

let svc = "src/Orders/OrderService.cs"
let tests = "tests/Shop.Tests/OrderServiceTests.cs"

let plannedChanges =
    [ Extend(svc, [ { Name = "OrderService.Refund"; Kind = Member; Sig = "public bool Refund(int id)"; Does = "Refunds an order and reports whether it worked."; Covers = [] } ])
      Alter($"{svc}#OrderService.Cancel", None, "Raises an OrderCancelled event after cancelling.")
      Extend(tests, [ { Name = "OrderServiceTests.Refund_returns_false_for_unknown_id"; Kind = Member; Sig = "public void Refund_returns_false_for_unknown_id()"; Does = "Checks that refunding an unknown order fails."; Covers = [ 1 ] } ]) ]

let criteria = [ "Refunding an unknown order fails without charging anyone." ]

/// A story whose acceptance criteria section lists the given criteria.
let storyWith (cs: string list) =
    "As a customer I want refunds.\n\nAcceptance criteria:\n" + String.concat "\n" (cs |> List.map (fun c -> "- " + c)) + "\n\nNotes: none."

let ok = function
    | Result.Ok v -> v
    | Result.Error e -> failwithf "unexpected error: %s" e

let readyPlan id =
    let repo, _ = newRepo ()
    Engine.start repo id "Refunds" "Customers can't refund." 30 (storyWith criteria) |> ok |> ignore
    Engine.add id plannedChanges |> ok |> ignore
    Engine.inspect id $"{svc}#OrderService.Cancel" |> ok |> ignore
    Engine.update id None None None (Some [ "CS.XmlDoc"; "TEST.Names" ]) None |> ok |> ignore
    repo

[<Fact>]
let ``add keeps valid changes and rejects unknown handles with a suggestion`` () =
    let repo, _ = newRepo ()
    Engine.start repo "E-1" "t" "w" 10 (storyWith criteria) |> ok |> ignore
    let r = Engine.add "E-1" [ yield! plannedChanges; Alter($"{svc}#OrderService.Cancle", None, "Logs the cancellation.") ] |> ok
    Assert.Equal(3, r.Accepted)
    let e = Assert.Single r.Errors
    Assert.Contains("changes[3]: unknown symbol", e)
    Assert.Contains($"did you mean: {svc}#OrderService.Cancel", e)

[<Fact>]
let ``add rejects new symbols that already exist and alters that say nothing`` () =
    let repo, _ = newRepo ()
    Engine.start repo "E-2" "t" "w" 10 "A story with no criteria." |> ok |> ignore
    let r =
        Engine.add "E-2"
            [ Extend(svc, [ { Name = "OrderService.Place"; Kind = Member; Sig = "public int Place()"; Does = "Places an order."; Covers = [] } ])
              Alter($"{svc}#OrderService.Place", None, "return qty * 2;")
              NewFile(svc, "dup", []) ]
        |> ok
    Assert.Equal(0, r.Accepted)
    Assert.Equal(3, r.Errors.Length)

[<Fact>]
let ``does and change must be one plain-English sentence, not code`` () =
    let repo, _ = newRepo ()
    Engine.start repo "E-5" "t" "w" 10 "A story with no criteria." |> ok |> ignore
    let sym does = { Name = "OrderService.Refund"; Kind = Member; Sig = "public bool Refund(int id)"; Does = does; Covers = [] }
    let r =
        Engine.add "E-5"
            [ Extend(svc, [ sym "call this.ledger.Reverse(id); return true;" ])
              Alter($"{svc}#OrderService.Cancel", None, "if (id == 0) throw")
              Alter($"{svc}#OrderService.Place", None, "")
              Touch("README.md", String.replicate 200 "a") ]
        |> ok
    Assert.Equal(0, r.Accepted)
    Assert.Contains("plain English, not code", r.Errors[0])
    Assert.Contains("plain English, not code", r.Errors[1])
    Assert.Contains("say in one plain-English sentence", r.Errors[2])
    Assert.Contains("under 160 characters", r.Errors[3])
    let fine = Engine.add "E-5" [ Extend(svc, [ sym "Reverses the charge and reports whether the refund went through." ]) ] |> ok
    Assert.Equal(1, fine.Accepted)

[<Fact>]
let ``add rejects names that disagree with their signature and says which name to use`` () =
    let repo, _ = newRepo ()
    Engine.start repo "E-3" "t" "w" 10 "A story with no criteria." |> ok |> ignore
    let r = Engine.add "E-3" [ Extend(tests, [ { Name = "OrderServiceTests.Refunds_work"; Kind = Member; Sig = "public void Refund_works()"; Does = "Checks refunds work."; Covers = [] } ]) ] |> ok
    let e = Assert.Single r.Errors
    Assert.Contains("name it 'OrderServiceTests.Refund_works'", e)

[<Fact>]
let ``add rejects a no-op newSig and alters aimed at a whole type`` () =
    let repo, _ = newRepo ()
    Engine.start repo "E-4" "t" "w" 10 "A story with no criteria." |> ok |> ignore
    let r =
        Engine.add "E-4"
            [ Alter($"{svc}#OrderService.Place", Some "public int Place(string sku, int qty)", "Validates the quantity first.")
              Alter($"{svc}#OrderService", None, "Changes how cancelling works.") ]
        |> ok
    Assert.Equal(0, r.Accepted)
    Assert.Contains("newSig equals the current signature", r.Errors[0])
    Assert.Contains("is a type; alter the members", r.Errors[1])
    Assert.Contains($"{svc}#OrderService.Cancel", r.Errors[1])

[<Fact>]
let ``check gates inspection, story criteria and standards, and publish refuses until ready`` () =
    let repo, _ = newRepo ()
    Engine.start repo "G-1" "t" "w" 30 (storyWith (criteria @ [ "A refund is logged." ])) |> ok |> ignore
    Engine.add "G-1" plannedChanges |> ok |> ignore
    let report = Engine.check (Engine.Store.tryGet "G-1" |> ok)
    Assert.False report.Ready
    let codes = report.Findings |> List.filter (fun f -> f.Severity = Fail) |> List.map (fun f -> f.Code) |> List.sort
    Assert.Equal<string list>([ "criteria"; "inspect"; "standard"; "standard" ], codes)
    Assert.Contains(report.Findings, fun f -> f.Code = "criteria" && f.Message.Contains "criterion 2 has no test")
    match Engine.publish "G-1" "none" with
    | Result.Error e -> Assert.StartsWith("not ready", e)
    | Result.Ok _ -> failwith "publish should refuse"

[<Fact>]
let ``criteria are read from the common ways stories are written`` () =
    let md = "As a user I want X.\n\n## Acceptance Criteria\n- Saves the draft\n- Shows a toast\n  that fades after 3s\n\n## Notes\n- not a criterion"
    Assert.Equal<string list>([ "Saves the draft"; "Shows a toast that fades after 3s" ], Engine.parseCriteria md)
    let ado = "Story text.\nAcceptance criteria:\n1. Login works\n2) Logout works\n[x] Session expires"
    Assert.Equal<string list>([ "Login works"; "Logout works"; "Session expires" ], Engine.parseCriteria ado)
    let gherkin = "**Acceptance criteria**\nGiven a cart\nWhen I check out\nThen I get a receipt\nGiven an empty cart\nThen checkout is disabled"
    Assert.Equal<string list>(
        [ "Given a cart When I check out Then I get a receipt"; "Given an empty cart Then checkout is disabled" ],
        Engine.parseCriteria gherkin)
    Assert.Empty(Engine.parseCriteria "As a user I want X so that Y.\n- a bullet that is not under a criteria heading")

[<Fact>]
let ``a story registered by the person wins over the agent's copy`` () =
    let repo, _ = newRepo ()
    Tools.preplanPrompt (storyWith [ "Refunds are logged."; "Refunds over 100 need approval." ]) "S-1" |> ignore
    let p = Engine.start repo "S-1" "t" "w" 10 "A paraphrased story without its criteria." |> ok
    Assert.Equal<string list>([ "Refunds are logged."; "Refunds over 100 need approval." ], p.Criteria)
    Assert.Contains("Refunds over 100 need approval.", p.Story)

[<Fact>]
let ``covers must point at real criteria and only tests can cover`` () =
    let repo, _ = newRepo ()
    Engine.start repo "C-1" "t" "w" 10 (storyWith criteria) |> ok |> ignore
    let test covers = { Name = "OrderServiceTests.Refund_logs"; Kind = Member; Sig = "public void Refund_logs()"; Does = "Checks refunds are logged."; Covers = covers }
    let r =
        Engine.add "C-1"
            [ Extend(tests, [ test [ 3 ] ])
              Extend(svc, [ { Name = "OrderService.Refund"; Kind = Member; Sig = "public bool Refund(int id)"; Does = "Refunds an order."; Covers = [ 1 ] } ]) ]
        |> ok
    Assert.Equal(0, r.Accepted)
    Assert.Contains("covers [3] but the story has criteria 1-1", r.Errors[0])
    Assert.Contains("only tests can cover criteria", r.Errors[1])
    let repo2, _ = newRepo ()
    Engine.start repo2 "C-2" "t" "w" 10 "A story with no criteria." |> ok |> ignore
    let r2 = Engine.add "C-2" [ Extend(tests, [ test [ 1 ] ]) ] |> ok
    Assert.Contains("the story has no criteria; omit covers", Assert.Single r2.Errors)

[<Fact>]
let ``without story criteria the tests are the acceptance, and a plan with no tests is flagged`` () =
    let repo, _ = newRepo ()
    Engine.start repo "C-3" "t" "w" 10 "A story with no criteria." |> ok |> ignore
    Engine.add "C-3" [ plannedChanges[0] ] |> ok |> ignore
    Engine.update "C-3" None None None (Some [ "CS.XmlDoc" ]) None |> ok |> ignore
    let r = Engine.check (Engine.Store.tryGet "C-3" |> ok)
    Assert.True(r.Ready, sprintf "%A" r.Findings)
    Assert.Contains(r.Findings, fun f -> f.Severity = Warn && f.Code = "tests")

[<Fact>]
let ``story criteria render above the changes with the tests that cover them`` () =
    readyPlan "R-1" |> ignore
    let md = Engine.render (Engine.Store.tryGet "R-1" |> ok)
    Assert.Contains("1. Refunding an unknown order fails without charging anyone. ✓ `Refund_returns_false_for_unknown_id`", md)
    Assert.Contains("Checks that refunding an unknown order fails. _(covers 1)_", md)
    Assert.True(md.IndexOf "**Story criteria**" < md.IndexOf "OrderService.cs")

[<Theory>]
[<InlineData("tests/Shop.Tests/A.cs", true)>]
[<InlineData("src/client/test/runtime.test.ts", true)>]
[<InlineData("src/a.spec.js", true)>]
[<InlineData("pkg/thing_test.go", true)>]
[<InlineData("app/test_models.py", true)>]
[<InlineData("src/Shop/OrderServiceTests.cs", true)>]
[<InlineData("src/Orders/OrderService.cs", false)>]
[<InlineData("src/latest.ts", false)>]
let ``test files are recognised across languages`` (path: string, expected: bool) = Assert.Equal(expected, Engine.isTestPath path)

[<Fact>]
let ``publishing to an issue writes the pre-PR files and the issue body round-trips the plan`` () =
    readyPlan "I-1" |> ignore
    let p, msg = Engine.publish "I-1" "issue" |> ok
    Assert.Contains("publish_storyplan", msg)
    let dir = Path.Combine(Standards.home (), "issues")
    let issue = File.ReadAllText(Path.Combine(dir, "I-1.md"))
    Assert.Equal("[pre-PR] I-1: Refunds", File.ReadAllText(Path.Combine(dir, "I-1.title")))
    Assert.Contains("```mermaid", issue)
    Assert.Contains("ac1[\"1. Refunding an unknown order fails without charging anyone.\"]:::ac", issue)
    Assert.Contains("ac1 --> t0", issue)
    Assert.Contains("✅ 1/1 criteria have a test", issue)
    Assert.Contains("  // Raises an OrderCancelled event after cancelling.", issue)
    Assert.Contains("! public void Cancel(int id)", issue)
    Assert.Contains("+ test 'Refund returns false for unknown id'   ✓ #1", issue)
    Assert.Equal(Result.Ok p, Engine.IssueBody.extract issue)
    let md = Engine.renderIssue p
    // A plan string containing a fence can't break out of the embedded block.
    let hostile = { p with Why = "Uses ```` and ``` in prose." }
    let body = Engine.IssueBody.build md hostile
    Assert.Equal(Result.Ok hostile, Engine.IssueBody.extract body)
    Assert.Equal(Result.Ok hostile, Engine.IssueBody.extract (body.Replace("\n", "\r\n")))
    match Engine.IssueBody.extract md with
    | Result.Error e -> Assert.Contains("missing a `json storyplan-v1` block", e)
    | Result.Ok _ -> failwith "a body without the marker has no plan"

[<Fact>]
let ``a complete plan is ready and publishes into the repo`` () =
    let repo = readyPlan "G-2"
    let report = Engine.check (Engine.Store.tryGet "G-2" |> ok)
    Assert.True(report.Ready, sprintf "%A" report.Findings)
    let p, msg = Engine.publish "G-2" "repo" |> ok
    Assert.Equal(Published, p.Status)
    Assert.True(File.Exists(Path.Combine(repo, ".stories", "G-2.plan.json")), msg)
    Assert.Contains("- + `public bool Refund(int id)`: Refunds an order and reports whether it worked.", File.ReadAllText(Path.Combine(repo, ".stories", "G-2.plan.md")))
    match Engine.add "G-2" plannedChanges with
    | Result.Error e -> Assert.Contains("published", e)
    | Result.Ok _ -> failwith "published plans are frozen"

let implement repo (extraTest: string) =
    git repo [ "checkout"; "-q"; "-b"; "feature" ]
    let text = File.ReadAllText(Path.Combine(repo, svc))
    let text =
        text.Replace("    public void Cancel(int id)\n    {\n    }", "    public void Cancel(int id)\n    {\n        Events.Raise(\"OrderCancelled\");\n    }")
            .Replace("\n}\n", "\n\n    /// <summary>Refunds an order.</summary>\n    public bool Refund(int id)\n    {\n        return false;\n    }\n}\n")
    write repo svc text
    let t = File.ReadAllText(Path.Combine(repo, tests))
    write repo tests (t.Replace("\n}\n", $"\n\n    [Fact]\n    public void {extraTest}()\n    {{\n        Assert.False(new OrderService().Refund(9));\n    }}\n}}\n"))
    commit repo "implement"

[<Fact>]
let ``verify passes a faithful implementation`` () =
    let repo = readyPlan "V-1"
    implement repo "Refund_returns_false_for_unknown_id" |> ignore
    let r = Engine.verify (Engine.Store.tryGet "V-1" |> ok) "main" "feature" None
    Assert.True(r.Conforms, sprintf "%A" r.Findings)
    Assert.Empty(r.Findings |> List.filter (fun f -> f.Severity <> Pass))
    Assert.Equal(3, r.Findings.Length)

[<Fact>]
let ``verify catches missing work, unplanned files, unplanned edits and standards`` () =
    let repo = readyPlan "V-2"
    implement repo "RefundTest" |> ignore
    let text = File.ReadAllText(Path.Combine(repo, svc)).Replace("return qty;", "return qty * 2;")
    write repo svc text
    write repo "src/Orders/Extra.cs" "// Licensed under the MIT license.\npublic class Extra { }\n"
    commit repo "drift" |> ignore
    let r = Engine.verify (Engine.Store.tryGet "V-2" |> ok) "main" "feature" None
    Assert.False r.Conforms
    let has sev (code: string) (text: string) =
        r.Findings |> List.exists (fun f -> f.Severity = sev && f.Code = code && f.Message.Contains text)
    Assert.True(has Fail "missing" "Refund_returns_false_for_unknown_id", sprintf "%A" r.Findings)
    Assert.True(has Fail "unplanned" "src/Orders/Extra.cs", sprintf "%A" r.Findings)
    Assert.True(has Warn "unplanned" "OrderService.Place", sprintf "%A" r.Findings)
    Assert.True(has Warn "unplanned" "OrderServiceTests.RefundTest", sprintf "%A" r.Findings)
    Assert.True(has Warn "standard" "TEST.Names", sprintf "%A" r.Findings)
    Assert.True(has Warn "standard" "CS.XmlDoc: src/Orders/Extra.cs:2", sprintf "%A" r.Findings)

[<Fact>]
let ``plan_add over the tool surface keeps valid elements and reports original indices`` () =
    let repo, _ = newRepo ()
    Engine.start repo "T-1" "t" "w" 10 "A story with no criteria." |> ok |> ignore
    let args =
        JsonNode.Parse($"""{{"id":"T-1","changes":[
          {{"kind":"bogus"}},
          {{"kind":"touch","file":"README.md","why":"doc"}},
          {{"kind":"alter","symbol":"{svc}#OrderService.Nope","change":"Logs it."}}]}}""") :?> JsonObject
    let r = Tools.call "plan_add" args
    Assert.False r.IsError
    Assert.Contains("accepted 1 of 3", r.Text)
    Assert.Contains("error changes[0]: unknown kind 'bogus'", r.Text)
    Assert.Contains("error changes[2]: unknown symbol", r.Text)

[<Fact>]
let ``every tool publishes a schema object and unknown tools fail cleanly`` () =
    for t in Tools.all do
        let s = Tools.inputSchema t
        Assert.Equal("object", (s["type"]).GetValue<string>())
    let r = Tools.call "nope" (JsonObject())
    Assert.True r.IsError
