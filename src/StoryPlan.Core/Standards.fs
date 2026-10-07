/// Coding standards as data. Rules live in <repo>/.storyplan/standards.json, or in
/// $STORYPLAN_HOME/standards/<repo-folder>.json when the repo shouldn't carry the file.
module StoryPlan.Standards

open System
open System.IO
open System.Text.RegularExpressions
open StoryPlan.Model

let home () =
    match Environment.GetEnvironmentVariable "STORYPLAN_HOME" with
    | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".storyplan")
    | h -> h

let candidates (root: string) =
    [ Path.Combine(root, ".storyplan", "standards.json")
      Path.Combine(home (), "standards", Path.GetFileName(Path.TrimEndingDirectorySeparator root) + ".json") ]

let load (root: string) : Result<Rule list, string> =
    match candidates root |> List.tryFind File.Exists with
    | None -> Result.Ok []
    | Some path ->
        match Json.deserialize<Rule list> (File.ReadAllText path) with
        | Result.Ok rules -> Result.Ok rules
        | Result.Error e -> Result.Error $"{path}: {e}"

let globMatch (glob: string) (path: string) =
    let rx =
        "^" + Regex.Escape(glob).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$"
    Regex.IsMatch(path, rx)

/// Violations of one rule in one file version, looking only at changed line numbers (1-based).
let evaluate (rule: Rule) (path: string) (lines: string[]) (changed: int list) =
    let at i = if i >= 1 && i <= lines.Length then lines[i - 1] else ""
    match rule.Check with
    | None -> []
    | Some check ->
        match check with
        | Forbid(pattern, unless) ->
            [ for i in changed do
                  let l = at i
                  if Regex.IsMatch(l, pattern) && not (unless |> Option.exists (fun u -> Regex.IsMatch(l, u))) then
                      $"{path}:{i} {l.Trim()}" ]
        | PrecededBy(line, prev, skipRx) ->
            [ for i in changed do
                  if Regex.IsMatch(at i, line) then
                      let mutable j = i - 1
                      while j >= 1 && (skipRx |> Option.exists (fun s -> Regex.IsMatch(at j, s))) do
                          j <- j - 1
                      if j < 1 || not (Regex.IsMatch(at j, prev)) then $"{path}:{i} {(at i).Trim()}" ]
        | FirstLine text ->
            if List.contains 1 changed && (at 1).Trim() <> text then [ $"{path}:1 expected '{text}'" ] else []
        | CaptureMatches(line, capture) ->
            [ for i in changed do
                  let m = Regex.Match(at i, line)
                  if m.Success && m.Groups.Count > 1 && not (Regex.IsMatch(m.Groups[1].Value, capture)) then
                      $"{path}:{i} '{m.Groups[1].Value}' does not match /{capture}/" ]
