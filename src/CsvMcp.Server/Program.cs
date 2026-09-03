using CsvMcp.Server.Configuration;
using CsvMcp.Server.Instructions;
using CsvMcp.Server.Services.Csv;
using CsvMcp.Server.Services.Maintenance;
using CsvMcp.Server.Services.Storage;
using CsvMcp.Server.Tools;
using CsvMcp.Server.Web;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------

builder.Services.Configure<CsvStorageOptions>(builder.Configuration.GetSection(CsvStorageOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICsvStoragePaths, CsvStoragePaths>();
builder.Services.AddSingleton<ICsvFormatter, CsvFormatter>();
builder.Services.AddSingleton<ICsvFileRepository, CsvFileRepository>();
builder.Services.AddSingleton<DownloadUrlBuilder>();

// Background maintenance: sweeps stale staging files out of the temp directory.
builder.Services.AddSingleton<TempFileCleanupService>();
builder.Services.AddHostedService(static sp => sp.GetRequiredService<TempFileCleanupService>());

// Needed by DownloadUrlBuilder to derive the public base URL from the current request.
builder.Services.AddHttpContextAccessor();

// ---------------------------------------------------------------------------
// MCP server
// ---------------------------------------------------------------------------

string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

// Single source of truth for the tool set: the catalog is used for registration,
// and the global usage guide is generated from it so the instructions always cover every tool.
// NOTE: keep the variable's static type as IEnumerable<Type> (not IReadOnlyList<Type> or Type[]):
// WithTools has both a generic overload (WithTools<T>(T target)) and a non-generic one
// (WithTools(IEnumerable<Type>)); a more specific static type would bind to the generic overload
// and silently register nothing.
IEnumerable<Type> toolTypes =
[
    typeof(CreateCsvTool),
    typeof(AppendCsvRowsTool),
    typeof(ListCsvFilesTool),
    typeof(GetCsvContentTool),
    typeof(GetCsvDownloadTool),
    typeof(DeleteCsvFileTool),
];

IReadOnlyList<ToolDescriptor> toolCatalog = ToolCatalog.FromTypes(toolTypes);
string instructions = GlobalUsageGuide.Build(toolCatalog);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "csv-mcp",
            Title = "CSV MCP Server",
            Version = version,
            Description = "Generates, stores, reads and downloads CSV files over the Streamable HTTP transport.",
        };

        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability(),
        };

        // Global usage guide covering every registered tool; served to clients with the server instructions.
        options.ServerInstructions = instructions;
    })
    .WithTools(toolTypes)
    .WithHttpTransport();

WebApplication app = builder.Build();

// ---------------------------------------------------------------------------
// HTTP endpoints
// ---------------------------------------------------------------------------

app.MapGet("/", () => Results.Json(new
{
    name = "csv-mcp",
    version,
    mcpEndpoint = "/mcp",
    downloadEndpoint = "/download/{fileId}",
    instructions = "The global usage guide is served via the MCP server instructions.",
}));

app.MapGet("/download/{fileId}", DownloadEndpoints.HandleAsync);

// MCP Streamable HTTP endpoint (stateless; POST JSON-RPC).
app.MapMcp("/mcp");

// Startup self-check: every registered tool must be documented in the global usage guide.
foreach (ToolDescriptor tool in toolCatalog)
{
    if (!instructions.Contains($"### `{tool.Name}`", StringComparison.Ordinal))
    {
        app.Logger.LogWarning("Tool '{ToolName}' is missing from the global usage guide.", tool.Name);
    }
}

app.Run();
