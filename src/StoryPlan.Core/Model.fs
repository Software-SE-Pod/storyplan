/// The plan model. These types are the single source of truth: the JSON wire format, the JSON Schema
/// that MCP clients and UIs consume, and every validation rule are derived from them.
module StoryPlan.Model

open System.ComponentModel

type Kind =
    | Type
    | Member
    | Func
    | Value

[<Description "A symbol the plan introduces. name is qualified the way the index reports it, e.g. Type.Member.">]
type NewSym =
    { Name: string
      Kind: Kind
      [<Description "Exact declaration line as it will appear in code">]
      Sig: string
      [<Description "One plain-English sentence saying what it does, e.g. 'Returns false instead of throwing when the payload is malformed.' No code.">]
      Does: string
      [<Description "Tests only: 1-based numbers of the story criteria this test proves, e.g. [1, 3]. Omit for non-tests or when the story has no criteria.">]
      Covers: int list }

/// Symbol handles are "path#Qualified.Name" (e.g. "src/Orders/OrderService.cs#OrderService.Place").
/// They come from find_symbol/repo_map, never from guessing.
[<Description "One planned change. Symbols are handles 'path#Qualified.Name' returned by find_symbol or repo_map. why/change are one plain-English sentence, no code.">]
type Change =
    | NewFile of path: string * why: string * syms: NewSym list
    | Extend of file: string * syms: NewSym list
    | Alter of symbol: string * newSig: string option * change: string
    | Remove of symbol: string * why: string
    | RemoveFile of file: string * why: string
    | Touch of file: string * why: string

type Status =
    | Draft
    | Ready
    | Published

type Plan =
    { Id: string
      Title: string
      Why: string
      /// Absolute path of the repository root the plan targets.
      Repo: string
      /// Commit the plan was made against.
      Sha: string
      /// The user story exactly as written. The server reads the acceptance criteria out of it.
      Story: string
      /// Acceptance criteria parsed from the story (written by a person, never by the planner).
      /// When present, every one must be covered by a test the plan adds. When absent, the tests are the acceptance.
      Criteria: string list
      /// Standard rule IDs the implementation must follow.
      Standards: string list
      Changes: Change list
      /// Expected changed lines. Verify flags a diff over 1.5x as scope creep.
      Loc: int
      /// Globs the implementation may touch without the plan naming them (lockfiles, snapshots).
      Allow: string list
      /// Symbol handles the planner actually inspected; Alter/Remove targets must be in here before publish.
      Inspected: string list
      Status: Status }

/// A deterministic check over the lines a PR changed. Data, not code, so any repo language works.
[<Description "Deterministic check run on changed lines only. Regexes are .NET syntax.">]
type Check =
    /// A changed line matching pattern is a violation unless it also matches unless.
    | Forbid of pattern: string * unless: string option
    /// A changed line matching line must have its nearest preceding line (skipping skip) match prev.
    | PrecededBy of line: string * prev: string * skip: string option
    /// When line 1 changes (or the file is new), it must equal text.
    | FirstLine of text: string
    /// On a changed line matching line, capture group 1 must match capture.
    | CaptureMatches of line: string * capture: string

type Rule =
    { Id: string
      Text: string
      /// path:line of code that already does this right. Cheaper than prose.
      Exemplar: string
      /// Glob of files the rule applies to, e.g. "src/**/*.cs".
      Glob: string
      Check: Check option }

type Severity =
    | Pass
    | Warn
    | Fail

type Finding =
    { Severity: Severity
      Code: string
      Message: string }

let finding sev code msg = { Severity = sev; Code = code; Message = msg }

module Handle =
    let make (path: string) (name: string) = $"{path}#{name}"

    let split (h: string) =
        match h.LastIndexOf '#' with
        | i when i > 0 -> Some(h.Substring(0, i), h.Substring(i + 1))
        | _ -> None

module Change =
    let file =
        function
        | NewFile(p, _, _) -> p
        | Extend(f, _)
        | RemoveFile(f, _)
        | Touch(f, _) -> f
        | Alter(h, _, _)
        | Remove(h, _) -> Handle.split h |> Option.map fst |> Option.defaultValue h

    let added =
        function
        | NewFile(_, _, s)
        | Extend(_, s) -> s
        | _ -> []
