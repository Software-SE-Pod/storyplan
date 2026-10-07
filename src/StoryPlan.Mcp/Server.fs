/// MCP transport: maps StoryPlan.Tools onto the MCP SDK's low-level handlers so every tool's
/// inputSchema is the one generated from the F# types, and plan tools carry MCP Apps UI metadata.
module StoryPlan.Mcp.Server

// ServerCapabilities.Extensions (used to advertise MCP Apps) is marked experimental in the SDK.
#nowarn "57"

open System
open System.IO
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open StoryPlan

let version = "0.1.0"
let appMime = "text/html;profile=mcp-app"
let schemaUri = "storyplan://schema/plan"

let embedded (name: string) =
    let asm = Assembly.GetExecutingAssembly()
    let res = asm.GetManifestResourceNames() |> Array.find (fun n -> n.EndsWith name)
    use s = asm.GetManifestResourceStream res
    use r = new StreamReader(s)
    r.ReadToEnd()

let toElement (n: JsonNode) =
    use doc = JsonDocument.Parse(n.ToJsonString())
    doc.RootElement.Clone()

let toolDefinition (t: Tools.Tool) =
    let tool = Tool(Name = t.Name, Description = t.Description, InputSchema = toElement (Tools.inputSchema t))
    let ui = JsonObject()
    if t.Ui then ui["resourceUri"] <- JsonValue.Create Tools.uiUri
    ui["visibility"] <- JsonArray(t.Visibility |> List.map (fun v -> JsonValue.Create v :> JsonNode) |> Array.ofList)
    let meta = JsonObject()
    meta["ui"] <- ui
    tool.Meta <- meta
    tool

/// Plain-JSON form of a tool result, shared by the MCP handler and the HTTP host.
let resultJson (r: Tools.ToolResult) =
    let o = JsonObject()
    let content = JsonObject()
    content["type"] <- JsonValue.Create "text"
    content["text"] <- JsonValue.Create r.Text
    o["content"] <- JsonArray(content)
    if not (isNull r.View) then
        o["structuredContent"] <-
            match r.View with
            | :? JsonObject as v -> v.DeepClone()
            | v -> JsonObject(dict [ "items", v.DeepClone() ])
    o["isError"] <- JsonValue.Create r.IsError
    o

let callTool (name: string) (args: Collections.Generic.IDictionary<string, JsonElement>) =
    let a = JsonObject()
    if not (isNull args) then
        for KeyValue(k, v) in args do
            a[k] <- JsonNode.Parse(v.GetRawText())
    let r = Tools.call name a
    let result = CallToolResult()
    result.Content <- ResizeArray<ContentBlock>([ TextContentBlock(Text = r.Text) :> ContentBlock ])
    match (resultJson r)["structuredContent"] with
    | null -> ()
    | sc -> result.StructuredContent <- Nullable(toElement sc)
    result.IsError <- Nullable r.IsError
    result

let uiMeta () =
    let ui = JsonObject()
    ui["prefersBorder"] <- JsonValue.Create true
    ui["csp"] <- JsonObject()
    let meta = JsonObject()
    meta["ui"] <- ui
    meta

/// Registers StoryPlan's tools, resources and prompts on an MCP server builder (any transport).
let private configure (mcp: IMcpServerBuilder) =
    mcp
        .WithListToolsHandler(McpRequestHandler<ListToolsRequestParams, ListToolsResult>(fun _ _ ->
            ValueTask<ListToolsResult>(ListToolsResult(Tools = ResizeArray(Tools.all |> List.map toolDefinition)))))
        .WithCallToolHandler(McpRequestHandler<CallToolRequestParams, CallToolResult>(fun ctx _ ->
            ValueTask<CallToolResult>(callTool ctx.Params.Name ctx.Params.Arguments)))
        .WithListResourcesHandler(McpRequestHandler<ListResourcesRequestParams, ListResourcesResult>(fun _ _ ->
            let resources =
                [ Resource(Uri = Tools.uiUri, Name = "storyplan-plan", Title = "StoryPlan pre-PR", MimeType = appMime,
                           Description = "Interactive view of a plan: story criteria, changes, standards, gates")
                  Resource(Uri = schemaUri, Name = "storyplan-schema", Title = "Plan JSON Schema", MimeType = "application/schema+json") ]
            ValueTask<ListResourcesResult>(ListResourcesResult(Resources = ResizeArray resources))))
        .WithReadResourceHandler(McpRequestHandler<ReadResourceRequestParams, ReadResourceResult>(fun ctx _ ->
            let uri = ctx.Params.Uri
            let contents: ResourceContents =
                if uri = Tools.uiUri then TextResourceContents(Uri = uri, MimeType = appMime, Text = embedded "plan.html", Meta = uiMeta ())
                elif uri = schemaUri then
                    TextResourceContents(Uri = uri, MimeType = "application/schema+json",
                                         Text = (Json.schemaOf typeof<Model.Plan>).ToJsonString(JsonSerializerOptions(WriteIndented = true)))
                else raise (ArgumentException $"unknown resource {uri}")
            ValueTask<ReadResourceResult>(ReadResourceResult(Contents = ResizeArray [ contents ]))))
        .WithListPromptsHandler(McpRequestHandler<ListPromptsRequestParams, ListPromptsResult>(fun _ _ ->
            let prompt =
                Prompt(Name = "preplan", Title = "Pre-PR for a story",
                       Description = "Walk the agent through building, checking and showing a StoryPlan for a story",
                       Arguments = ResizeArray [ PromptArgument(Name = "id", Description = "Story id, e.g. STORY-12", Required = true)
                                                 PromptArgument(Name = "story", Description = "The user story text", Required = true) ])
            ValueTask<ListPromptsResult>(ListPromptsResult(Prompts = ResizeArray [ prompt ]))))
        .WithGetPromptHandler(McpRequestHandler<GetPromptRequestParams, GetPromptResult>(fun ctx _ ->
            let get k = match ctx.Params.Arguments with null -> "" | a -> (match a.TryGetValue k with | true, v -> v.ToString() | _ -> "")
            let text = Tools.preplanPrompt (get "story") (get "id")
            let msg = PromptMessage(Role = Role.User, Content = TextContentBlock(Text = text))
            ValueTask<GetPromptResult>(GetPromptResult(Description = "StoryPlan pre-PR workflow", Messages = ResizeArray [ msg ]))))
    |> ignore

let private options (o: McpServerOptions) =
    o.ServerInfo <- Implementation(Name = "storyplan", Title = "StoryPlan", Version = version)
    o.ServerInstructions <-
        "StoryPlan builds a typed, verifiable pre-PR for a user story. Use the preplan prompt or follow it: "
        + "plan_start, repo_map/find_symbol, get_symbol (plan=id) for everything you alter, standards, plan_add, plan_check, plan_show. Pass the full story text to plan_start; the server reads its acceptance criteria and tests cover them by number. "
        + "Never invent symbol handles; copy them from tool output."
    o.Capabilities <- ServerCapabilities(Extensions = dict [ "io.modelcontextprotocol/ui", box (JsonObject()) ])

/// MCP over stdio, for local agents (Copilot CLI, VS Code, Claude).
let run (args: string[]) : Task =
    let builder = Host.CreateApplicationBuilder(args)
    // stdout is the MCP channel; every log line goes to stderr.
    builder.Logging.ClearProviders() |> ignore
    builder.Logging.AddConsole(fun o -> o.LogToStandardErrorThreshold <- LogLevel.Trace) |> ignore
    builder.Logging.SetMinimumLevel LogLevel.Warning |> ignore
    builder.Services.AddMcpServer(options).WithStdioServerTransport() |> configure
    Tools.warmUp ()
    builder.Build().RunAsync()

/// MCP over streamable HTTP at /mcp, for hosts that run servers as plain processes (gh-aw on a runner).
/// Binds to the given address; there is no auth, so only bind where the network is already trusted.
let runHttp (host: string) (port: int) : Task =
    let builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder()
    Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseUrls(builder.WebHost, $"http://{host}:{port}") |> ignore
    builder.Logging.SetMinimumLevel LogLevel.Warning |> ignore
    builder.Services.AddMcpServer(options).WithHttpTransport(fun o -> o.Stateless <- true) |> configure
    let app = builder.Build()
    Microsoft.AspNetCore.Builder.McpEndpointRouteBuilderExtensions.MapMcp(app, "/mcp") |> ignore
    Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(app, "/healthz", Func<string>(fun () -> "ok")) |> ignore
    eprintfn "StoryPlan MCP on http://%s:%d/mcp" host port
    Tools.warmUp ()
    app.RunAsync()
