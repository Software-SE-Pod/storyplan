/// Git plumbing. Every read is against a commit, never the working tree, so results are reproducible.
module StoryPlan.Git

open System
open System.Diagnostics
open System.IO
open System.Text

let private start (repo: string) (args: string list) =
    let psi =
        ProcessStartInfo("git",
                         RedirectStandardOutput = true,
                         RedirectStandardError = true,
                         RedirectStandardInput = true,
                         UseShellExecute = false,
                         StandardOutputEncoding = Encoding.UTF8)
    psi.ArgumentList.Add "-C"
    psi.ArgumentList.Add repo
    psi.ArgumentList.Add "-c"
    psi.ArgumentList.Add "core.quotepath=off"
    for a in args do psi.ArgumentList.Add a
    Process.Start psi

let run (repo: string) (args: string list) =
    use p = start repo args
    p.StandardInput.Close()
    let err = p.StandardError.ReadToEndAsync()
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    if p.ExitCode <> 0 then failwithf "git %s: %s" (String.Join(" ", args)) (err.Result.Trim())
    out

let tryRun repo args = try Some(run repo args) with _ -> None

let lines (s: string) = s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n') |> Array.filter (fun l -> l <> "")

let revParse repo (rev: string) = (run repo [ "rev-parse"; "--verify"; rev + "^{commit}" ]).Trim()

let show repo sha (path: string) = tryRun repo [ "show"; $"{sha}:{path}" ]

let lsTree repo sha = lines (run repo [ "ls-tree"; "-r"; "--name-only"; sha ])

/// Reads many blobs through one `git cat-file --batch` process.
let readBlobs (repo: string) (sha: string) (paths: string[]) : Map<string, string> =
    use p = start repo [ "cat-file"; "--batch" ]
    let input = p.StandardInput
    let writer = Threading.Tasks.Task.Run(fun () ->
        for path in paths do input.Write($"{sha}:{path}\n")
        input.Close())
    let out = p.StandardOutput.BaseStream
    let readLine () =
        let buf = ResizeArray<byte>()
        let mutable b = out.ReadByte()
        while b <> -1 && b <> int '\n' do
            buf.Add(byte b)
            b <- out.ReadByte()
        Encoding.UTF8.GetString(buf.ToArray())
    let result =
        [ for path in paths do
              let header = readLine ()
              let parts = header.Split ' '
              if parts.Length = 3 && parts[1] = "blob" then
                  let size = int parts[2]
                  let data = Array.zeroCreate<byte> size
                  let mutable read = 0
                  while read < size do
                      read <- read + out.Read(data, read, size - read)
                  out.ReadByte() |> ignore
                  yield path, Encoding.UTF8.GetString data ]
    writer.Wait()
    p.WaitForExit()
    Map.ofList result

let mergeBase repo a b = (run repo [ "merge-base"; a; b ]).Trim()

/// (status letter, path) for every file changed between two commits.
let nameStatus repo a b =
    lines (run repo [ "diff"; "--name-status"; "--no-renames"; a; b ])
    |> Array.map (fun l -> let p = l.Split '\t' in p[1], p[0])
    |> Map.ofArray

let diff repo a b (context: int) = run repo [ "diff"; $"-U{context}"; "--no-renames"; a; b ]

let changedLineCount repo a b =
    lines (run repo [ "diff"; "--numstat"; a; b ])
    |> Array.sumBy (fun l ->
        match l.Split '\t' with
        | [| x; y; _ |] when x <> "-" -> int x + int y
        | _ -> 0)

let isRepo (path: string) = Directory.Exists path && (tryRun path [ "rev-parse"; "--git-dir" ]).IsSome
