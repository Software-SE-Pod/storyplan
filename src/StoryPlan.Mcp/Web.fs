/// Local web host: the same plan UI outside chat, plus an MCP Apps host simulator (/harness) for testing the view.
/// Binds to loopback only, and only accepts JSON POSTs from its own origin.
module StoryPlan.Mcp.Web

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Http
open StoryPlan

let private html (content: string) = Results.Content(content, "text/html; charset=utf-8")

let run (port: int) : Task =
    let builder = WebApplication.CreateBuilder()
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}") |> ignore
    builder.Logging.SetMinimumLevel(LogLevel.Warning) |> ignore
    let app = builder.Build()
    let origin = $"http://127.0.0.1:{port}"

    app.MapGet("/", Func<IResult>(fun () -> html (Server.embedded "plan.html"))) |> ignore
    app.MapGet("/ui", Func<IResult>(fun () -> html (Server.embedded "plan.html"))) |> ignore
    app.MapGet("/harness", Func<IResult>(fun () -> html (Server.embedded "harness.html"))) |> ignore
    app.MapGet("/api/tools", Func<IResult>(fun () ->
        let arr = JsonArray()
        for t in Tools.all do
            let d = Server.toolDefinition t
            let o = JsonObject()
            o["name"] <- JsonValue.Create d.Name
            o["description"] <- JsonValue.Create d.Description
            o["inputSchema"] <- Tools.inputSchema t
            o["_meta"] <- d.Meta.DeepClone()
            arr.Add o
        Results.Content(arr.ToJsonString(), "application/json"))) |> ignore
    app.MapGet("/api/schema", Func<IResult>(fun () ->
        Results.Content((Json.schemaOf typeof<Model.Plan>).ToJsonString(), "application/json"))) |> ignore

    app.MapPost("/api/tools/{name}", Func<HttpContext, string, Task<IResult>>(fun ctx name ->
        task {
            let o = ctx.Request.Headers.Origin.ToString()
            if o <> "" && o <> origin && o <> $"http://localhost:{port}" then
                return Results.StatusCode 403
            elif not (ctx.Request.ContentType <> null && ctx.Request.ContentType.StartsWith "application/json") then
                return Results.StatusCode 415
            else
                use reader = new StreamReader(ctx.Request.Body)
                let! body = reader.ReadToEndAsync()
                let args =
                    match (if String.IsNullOrWhiteSpace body then null else JsonNode.Parse body) with
                    | :? JsonObject as a -> a
                    | _ -> JsonObject()
                let r = Tools.call name args
                return Results.Content((Server.resultJson r).ToJsonString(), "application/json")
        })) |> ignore

    eprintfn "StoryPlan UI on %s  (harness: %s/harness?plan=<id>)" origin origin
    Tools.warmUp ()
    app.RunAsync()
