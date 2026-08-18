using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/llm-proxy-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "LlmProxy API",
        Version = "v1",
        Description = "Expose Claude-compatible forwarding endpoints for Claude CLI, Hermes, and custom applications."
    });
});

builder.Services.AddHttpClient("upstream", (sp, client) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var timeout = config.GetValue("Proxy:Upstream:TimeoutSeconds", 180);
    client.Timeout = TimeSpan.FromSeconds(timeout);
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

var config = app.Configuration;
var routePrefix = config["Proxy:RoutePrefix"] ?? "claude-kiro-oauth";
var backend = config["Proxy:Backend"] ?? "KiroApi";
var upstreamBaseUrl = config["Proxy:Upstream:BaseUrl"] ?? "https://api.anthropic.com";
var upstreamApiKey = config["Proxy:Upstream:ApiKey"] ?? "";
var maxRetries = config.GetValue("Proxy:Upstream:MaxRetries", 3);
var cacheEnabled = config.GetValue("Proxy:Cache:Enabled", true);
var cacheDir = config["Proxy:Cache:Directory"] ?? "llm-cache";
var cacheTtlMinutes = config.GetValue("Proxy:Cache:TtlMinutes", 1440);
var maxSizeMB = config.GetValue("Proxy:Cache:MaxSizeMB", 500);
var kiroNodeCommand = config["Proxy:KiroApi:NodeCommand"] ?? "node";
var kiroRegion = config["Proxy:KiroApi:Region"] ?? "us-east-1";
var kiroEndpoint = config["Proxy:KiroApi:Endpoint"] ?? "https://q.us-east-1.amazonaws.com";
var kiroTokenFilePath = config["Proxy:KiroApi:TokenFilePath"] ?? @"C:\Users\Prove\.aws\sso\cache\kiro-auth-token.json";
var kiroRegistrationCacheDirectory = config["Proxy:KiroApi:RegistrationCacheDirectory"] ?? @"C:\Users\Prove\.aws\sso\cache";
var kiroContentCollectionEnabled = config.GetValue("Proxy:KiroApi:ContentCollectionEnabled", false);
var kiroAgentMode = config["Proxy:KiroApi:AgentMode"] ?? "vibe";
var configuredModels = config.GetSection("Proxy:Models:Available").Get<string[]>() ??
                       ["claude-sonnet-4", "claude-opus-4.1", "kiro-default"];
var defaultModel = config["Proxy:Models:Default"] ?? configuredModels.First();
var kiroHelperPath = Path.Combine(app.Environment.ContentRootPath, "kiro-api-helper.cjs");
var maxRequestBodyBytes = config.GetValue("Proxy:ContextTruncation:MaxRequestBodyBytes", 800_000);
var keepRecentMessages = config.GetValue("Proxy:ContextTruncation:KeepRecentMessages", 20);
var truncationEnabled = config.GetValue("Proxy:ContextTruncation:Enabled", true);

if (cacheEnabled && !Directory.Exists(cacheDir))
{
    Directory.CreateDirectory(cacheDir);
}

var usageDbPath = Path.Combine(app.Environment.ContentRootPath, "data", "token-usage.db");
Directory.CreateDirectory(Path.GetDirectoryName(usageDbPath)!);
var usageConnectionString = $"Data Source={usageDbPath}";
InitUsageDb(usageConnectionString);

Log.Information(
    "LLM Proxy 启动, 路由: /{RoutePrefix}/v1/messages, 后端: {Backend}, 上游: {Upstream}, KiroNode: {KiroNode}, 缓存: {Cache}",
    routePrefix,
    backend,
    upstreamBaseUrl,
    kiroNodeCommand,
    cacheEnabled ? "开启" : "关闭");

app.MapGet("/", () => Results.Redirect("/swagger"))
    .ExcludeFromDescription();


app.MapGet("/api/hello", () => Results.Ok(new { status = "ok" }))
    .WithName("ApiHello")
    .WithTags("System")
    .WithSummary("Connectivity check endpoint for Claude Code")
    .Produces(StatusCodes.Status200OK);

app.MapGet("/v1/oauth/hello", () => Results.Ok(new { status = "ok" }))
    .WithName("OAuthHello")
    .WithTags("System")
    .WithSummary("OAuth connectivity check endpoint for Claude Code")
    .Produces(StatusCodes.Status200OK);

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    backend,
    upstream = upstreamBaseUrl,
    kiroNodeCommand,
    kiroRegion,
    kiroEndpoint,
    cacheEnabled,
    cacheDir
}))
    .WithName("HealthCheck")
    .WithTags("System")
    .WithSummary("Health check")
    .WithDescription("Returns the current proxy health and active backend configuration.")
    .Produces(StatusCodes.Status200OK);

app.MapMethods("/debug/kiro/ping", new[] { "GET", "POST" }, async (HttpContext ctx) =>
{
    var logger = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();

    if (!backend.Equals("KiroApi", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new
        {
            error = "Kiro ping endpoint requires Proxy:Backend to be KiroApi",
            backend
        });
    }

    var prompt = "Reply with exactly pong";
    if (HttpMethods.IsGet(ctx.Request.Method))
    {
        var queryPrompt = ctx.Request.Query["prompt"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(queryPrompt))
        {
            prompt = queryPrompt;
        }
    }
    else if (ctx.Request.ContentLength is > 0)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var requestBody = await reader.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {
                var pingRequest = JsonSerializer.Deserialize<KiroPingRequest>(requestBody);
                if (!string.IsNullOrWhiteSpace(pingRequest?.Prompt))
                {
                    prompt = pingRequest.Prompt;
                }
            }
            catch (JsonException)
            {
                prompt = requestBody.Trim();
            }
        }
    }

    var syntheticClaudeRequest = JsonSerializer.Serialize(new
    {
        model = "kiro-ping",
        stream = false,
        messages = new[]
        {
            new
            {
                role = "user",
                content = prompt
            }
        }
    });

    var result = await ForwardToKiroApiAsync(
        syntheticClaudeRequest,
        kiroNodeCommand,
        kiroHelperPath,
        kiroRegion,
        kiroEndpoint,
        kiroTokenFilePath,
        kiroRegistrationCacheDirectory,
        kiroContentCollectionEnabled,
        kiroAgentMode,
        logger,
        ctx.RequestAborted);

    string? text = null;
    try
    {
        using var responseDoc = JsonDocument.Parse(result.Body);
        if (responseDoc.RootElement.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind == JsonValueKind.Array)
        {
            var firstText = contentElement.EnumerateArray()
                .FirstOrDefault(item =>
                    item.TryGetProperty("type", out var typeElement) &&
                    typeElement.GetString() == "text");
            if (firstText.ValueKind != JsonValueKind.Undefined &&
                firstText.TryGetProperty("text", out var textElement))
            {
                text = textElement.GetString();
            }
        }
    }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "解析 Kiro ping 响应失败");
    }

    return Results.Json(new
    {
        ok = result.StatusCode is >= 200 and < 300,
        backend,
        prompt,
        statusCode = result.StatusCode,
        text,
        raw = JsonSerializer.Deserialize<object>(result.Body)
    }, statusCode: result.StatusCode);
})
    .WithName("KiroPing")
    .WithTags("Debug")
    .WithSummary("Test Kiro connectivity")
    .WithDescription("""
Sends a small synthetic Claude-compatible request to Kiro and returns the parsed text plus the raw upstream response.

Example request body:
{
  "prompt": "Reply with exactly pong"
}

Example response:
{
  "ok": true,
  "backend": "KiroApi",
  "prompt": "Reply with exactly pong",
  "statusCode": 200,
  "text": "pong"
}
""")
    .Accepts<KiroPingRequest>("application/json")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status502BadGateway)
    .Produces(StatusCodes.Status503ServiceUnavailable);

// LLM 连通性测试：通用端点，不管后端是 KiroApi 还是 Anthropic 都能测
app.MapMethods("/llm/test", new[] { "GET", "POST" }, async (HttpContext ctx) =>
{
    var logger = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    var clientFactory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();

    // 解析 prompt
    var prompt = "请用一句话回答：1+1等于几？";
    if (HttpMethods.IsGet(ctx.Request.Method))
    {
        var q = ctx.Request.Query["prompt"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(q)) prompt = q;
    }
    else if (ctx.Request.ContentLength is > 0)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String)
                    prompt = p.GetString() ?? prompt;
            }
            catch (JsonException) { prompt = body.Trim(); }
        }
    }

    // 构造一个最小的 Claude Messages API 请求
    var requestJson = JsonSerializer.Serialize(new
    {
        model = defaultModel,
        max_tokens = 256,
        stream = false,
        system = "你是一个测试助手，请简短回答。",
        messages = new[] { new { role = "user", content = prompt } }
    });

    var sw = System.Diagnostics.Stopwatch.StartNew();
    ProxyResult result;

    try
    {
        if (backend.Equals("KiroApi", StringComparison.OrdinalIgnoreCase))
        {
            result = await ForwardToKiroApiAsync(
                requestJson, kiroNodeCommand, kiroHelperPath,
                kiroRegion, kiroEndpoint, kiroTokenFilePath,
                kiroRegistrationCacheDirectory, kiroContentCollectionEnabled,
                kiroAgentMode, logger, ctx.RequestAborted);
        }
        else
        {
            var effectiveApiKey = !string.IsNullOrEmpty(upstreamApiKey)
                ? upstreamApiKey
                : ctx.Request.Headers["x-api-key"].FirstOrDefault() ?? "";

            if (string.IsNullOrEmpty(effectiveApiKey))
            {
                sw.Stop();
                return Results.Json(new
                {
                    success = false, backend, prompt,
                    error = "No API key: 请在 Proxy:Upstream:ApiKey 配置或请求头传 x-api-key",
                    latencyMs = sw.ElapsedMilliseconds
                }, statusCode: 401);
            }

            var upstreamUrl = $"{upstreamBaseUrl.TrimEnd('/')}/v1/messages";
            var httpClient = clientFactory.CreateClient("upstream");
            using var req = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-api-key", effectiveApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");

            var resp = await httpClient.SendAsync(req, ctx.RequestAborted);
            var respBody = await resp.Content.ReadAsStringAsync(ctx.RequestAborted);
            result = new ProxyResult((int)resp.StatusCode, respBody);
        }
    }
    catch (Exception ex)
    {
        sw.Stop();
        logger.LogError(ex, "LLM 测试失败");
        return Results.Json(new
        {
            success = false, backend, prompt,
            error = ex.Message, errorType = ex.GetType().Name,
            latencyMs = sw.ElapsedMilliseconds,
            timestamp = DateTime.UtcNow
        }, statusCode: 502);
    }

    sw.Stop();

    // 解析响应文本
    string? responseText = null;
    var hasToolUse = false;
    try
    {
        using var doc = JsonDocument.Parse(result.Body);
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t))
                {
                    var type = t.GetString();
                    if (type == "text" && block.TryGetProperty("text", out var txt))
                        responseText = txt.GetString();
                    if (type == "tool_use")
                        hasToolUse = true;
                }
            }
        }
    }
    catch (JsonException) { /* 解析失败返回 raw */ }

    var ok = result.StatusCode is >= 200 and < 300;
    return Results.Json(new
    {
        success = ok,
        backend,
        model = defaultModel,
        prompt,
        response = responseText,
        hasToolUse,
        statusCode = result.StatusCode,
        latencyMs = sw.ElapsedMilliseconds,
        timestamp = DateTime.UtcNow,
        raw = ok ? null : (object?)result.Body
    }, statusCode: result.StatusCode);
})
    .WithName("LlmTest")
    .WithTags("Debug")
    .WithSummary("LLM 连通性测试")
    .WithDescription("""
发送一条简单消息到当前配置的 LLM 后端（KiroApi 或 Anthropic），验证完整链路是否正常。

GET /llm/test?prompt=你好
POST /llm/test  {"prompt": "你好"}

返回响应内容、延迟、后端类型等诊断信息。
""")
    .Produces(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status401Unauthorized)
    .Produces(StatusCodes.Status502BadGateway);

app.MapGet("/cache/stats", () =>
{
    if (!cacheEnabled || !Directory.Exists(cacheDir))
    {
        return Results.Ok(new { enabled = false });
    }

    var files = Directory.GetFiles(cacheDir, "*.json");
    var totalSize = files.Sum(f => new FileInfo(f).Length);
    var expiredCount = files.Count(f =>
        (DateTime.UtcNow - new FileInfo(f).LastWriteTimeUtc).TotalMinutes > cacheTtlMinutes);

    return Results.Ok(new
    {
        enabled = true,
        totalFiles = files.Length,
        totalSizeMB = Math.Round(totalSize / 1024.0 / 1024.0, 2),
        expiredFiles = expiredCount,
        ttlMinutes = cacheTtlMinutes
    });
});

app.MapPost("/cache/clear", () =>
{
    if (!Directory.Exists(cacheDir))
    {
        return Results.Ok(new { cleared = 0 });
    }

    var files = Directory.GetFiles(cacheDir, "*.json");
    foreach (var file in files)
    {
        File.Delete(file);
    }

    return Results.Ok(new { cleared = files.Length });
});

MapModelsRoute("/v1/models", "Default model discovery endpoint.");

// Token 用量统计 API
app.MapGet("/usage/stats", (HttpContext ctx) =>
{
    var granularity = ctx.Request.Query["granularity"].FirstOrDefault() ?? "day";
    var startStr = ctx.Request.Query["start"].FirstOrDefault();
    var endStr = ctx.Request.Query["end"].FirstOrDefault();
    var model = ctx.Request.Query["model"].FirstOrDefault();

    var now = DateTime.UtcNow;
    DateTime start, end;

    if (!string.IsNullOrEmpty(startStr) && DateTime.TryParse(startStr, out var parsedStart))
        start = parsedStart.ToUniversalTime();
    else
        start = granularity switch
        {
            "hour" => now.AddHours(-24),
            "week" => now.AddDays(-90),
            "month" => now.AddDays(-365),
            _ => now.AddDays(-30)
        };

    if (!string.IsNullOrEmpty(endStr) && DateTime.TryParse(endStr, out var parsedEnd))
        end = parsedEnd.ToUniversalTime();
    else
        end = now;

    var formatExpr = granularity switch
    {
        "hour" => "strftime('%Y-%m-%d %H:00', timestamp)",
        "week" => "strftime('%Y-W%W', timestamp)",
        "month" => "strftime('%Y-%m', timestamp)",
        _ => "strftime('%Y-%m-%d', timestamp)"
    };

    using var conn = new SqliteConnection(usageConnectionString);
    conn.Open();

    var sql = $@"
        SELECT {formatExpr} AS period, model,
               SUM(input_tokens) AS input_tokens,
               SUM(output_tokens) AS output_tokens,
               SUM(input_tokens + output_tokens) AS total_tokens,
               COUNT(*) AS request_count
        FROM token_usage
        WHERE timestamp >= @start AND timestamp <= @end
        {(string.IsNullOrEmpty(model) ? "" : "AND model = @model")}
        GROUP BY period, model
        ORDER BY period DESC, total_tokens DESC";

    using var cmd = new SqliteCommand(sql, conn);
    cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
    cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd HH:mm:ss"));
    if (!string.IsNullOrEmpty(model))
        cmd.Parameters.AddWithValue("@model", model);

    var rows = new List<object>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        rows.Add(new
        {
            period = reader.GetString(0),
            model = reader.GetString(1),
            inputTokens = reader.GetInt64(2),
            outputTokens = reader.GetInt64(3),
            totalTokens = reader.GetInt64(4),
            requestCount = reader.GetInt64(5)
        });
    }

    // 汇总
    var summSql = $@"
        SELECT SUM(input_tokens), SUM(output_tokens), SUM(input_tokens + output_tokens), COUNT(*)
        FROM token_usage
        WHERE timestamp >= @start AND timestamp <= @end
        {(string.IsNullOrEmpty(model) ? "" : "AND model = @model")}";

    using var summCmd = new SqliteCommand(summSql, conn);
    summCmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
    summCmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd HH:mm:ss"));
    if (!string.IsNullOrEmpty(model))
        summCmd.Parameters.AddWithValue("@model", model);

    using var summReader = summCmd.ExecuteReader();
    summReader.Read();

    return Results.Ok(new
    {
        granularity,
        start = start.ToString("yyyy-MM-dd HH:mm:ss"),
        end = end.ToString("yyyy-MM-dd HH:mm:ss"),
        summary = new
        {
            inputTokens = summReader.IsDBNull(0) ? 0 : summReader.GetInt64(0),
            outputTokens = summReader.IsDBNull(1) ? 0 : summReader.GetInt64(1),
            totalTokens = summReader.IsDBNull(2) ? 0 : summReader.GetInt64(2),
            requestCount = summReader.IsDBNull(3) ? 0 : summReader.GetInt64(3)
        },
        data = rows
    });
})
    .WithName("UsageStats")
    .WithTags("Usage")
    .WithSummary("Token 用量统计")
    .WithDescription("按小时/天/周/月维度聚合查询 token 用量。参数: granularity=hour|day|week|month, start, end, model")
    .Produces(StatusCodes.Status200OK);

app.MapGet("/usage/models", () =>
{
    using var conn = new SqliteConnection(usageConnectionString);
    conn.Open();
    using var cmd = new SqliteCommand("SELECT DISTINCT model FROM token_usage ORDER BY model", conn);
    using var reader = cmd.ExecuteReader();
    var models = new List<string>();
    while (reader.Read()) models.Add(reader.GetString(0));
    return Results.Ok(models);
})
    .WithName("UsageModels")
    .WithTags("Usage")
    .WithSummary("获取有用量记录的模型列表")
    .Produces(StatusCodes.Status200OK);

app.MapGet("/usage", (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/html; charset=utf-8";
    return Results.Content(GetUsageDashboardHtml(), "text/html");
})
    .WithName("UsageDashboard")
    .WithTags("Usage")
    .WithSummary("Token 用量统计面板")
    .ExcludeFromDescription();
MapModelsRoute("/claude/v1/models", "Model discovery endpoint for Claude integrations.");
MapModelsRoute("/hermes/v1/models", "Model discovery endpoint for Hermes integrations.");
MapModelsRoute("/custom/v1/models", "Model discovery endpoint for custom applications.");
MapModelsRoute($"/{routePrefix}/v1/models", "Legacy model discovery endpoint kept for existing integrations.");

MapClaudeProxyRoute("/v1/messages", "Default Claude-compatible messages endpoint for Claude CLI or any client that can point its base URL here.");
MapClaudeProxyRoute($"/{routePrefix}/v1/messages", "Legacy route kept for existing integrations.");
MapClaudeProxyRoute("/claude/v1/messages", "Explicit route for Claude CLI style integrations.");
MapClaudeProxyRoute("/hermes/v1/messages", "Explicit route for Hermes integrations.");
MapClaudeProxyRoute("/custom/v1/messages", "Explicit route for custom applications.");

// OpenAI Responses API 兼容端点 —— 支持 Codex CLI
MapResponsesApiRoute("/v1/responses", "OpenAI Responses API compatible endpoint for Codex CLI and other OpenAI-compatible clients.");

var port = config.GetValue("Proxy:ListenPort", 3000);
app.Urls.Add($"http://0.0.0.0:{port}");
Log.Information("LLM Proxy 监听端口: {Port}", port);

// 后台定时刷新 Kiro token，防止长时间无请求导致 token 过期掉线
if (backend.Equals("KiroApi", StringComparison.OrdinalIgnoreCase))
{
    var refreshIntervalMinutes = config.GetValue("Proxy:KiroApi:TokenRefreshIntervalMinutes", 45);
    _ = Task.Run(async () =>
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(refreshIntervalMinutes));
        Log.Information("Kiro token 后台刷新已启动，间隔: {Interval} 分钟", refreshIntervalMinutes);
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                var refreshResult = await RefreshKiroTokenAsync(
                    kiroNodeCommand, kiroHelperPath, kiroRegion, kiroEndpoint,
                    kiroTokenFilePath, kiroRegistrationCacheDirectory,
                    kiroContentCollectionEnabled, kiroAgentMode);
                if (refreshResult.Success)
                {
                    Log.Information("Kiro token 后台刷新成功，新过期时间: {ExpiresAt}", refreshResult.ExpiresAt);
                }
                else
                {
                    Log.Warning("Kiro token 后台刷新失败: {Error}", refreshResult.Error);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Kiro token 后台刷新异常");
            }
        }
    });
}

app.Run();

void MapClaudeProxyRoute(string pattern, string description)
{
    RouteHandlerBuilder builder = app.MapPost(pattern, async (HttpContext ctx, CancellationToken _) =>
        await HandleClaudeMessagesAsync(ctx));

    builder
        .WithName($"ProxyMessages_{SanitizeEndpointName(pattern)}")
        .WithTags("Proxy")
        .WithSummary("Forward Claude-compatible messages")
        .WithDescription($$"""
{{description}}

Example request body:
{
  "model": "{{defaultModel}}",
  "stream": false,
  "messages": [
    {
      "role": "user",
      "content": "Reply with exactly hello"
    }
  ]
}

Example response:
{
  "id": "msg_example",
  "type": "message",
  "role": "assistant",
  "model": "{{defaultModel}}",
  "content": [
    {
      "type": "text",
      "text": "hello"
    }
  ],
  "stop_reason": "end_turn",
  "stop_sequence": null,
  "usage": {
    "input_tokens": 0,
    "output_tokens": 0
  }
}
""")
        .Accepts<ClaudeProxyRequest>("application/json")
        .Produces<ClaudeProxyResponse>(StatusCodes.Status200OK, "application/json")
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status499ClientClosedRequest)
        .Produces(StatusCodes.Status500InternalServerError)
        .Produces(StatusCodes.Status502BadGateway)
        .Produces(StatusCodes.Status503ServiceUnavailable);
}

void MapModelsRoute(string pattern, string description)
{
    app.MapGet(pattern, async (HttpContext ctx) =>
        {
            var logger = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
            var dynamicModels = backend.Equals("KiroApi", StringComparison.OrdinalIgnoreCase)
                ? await ForwardToKiroModelsAsync(
                    kiroNodeCommand,
                    kiroHelperPath,
                    kiroRegion,
                    kiroEndpoint,
                    kiroTokenFilePath,
                    kiroRegistrationCacheDirectory,
                    kiroContentCollectionEnabled,
                    kiroAgentMode,
                    logger,
                    ctx.RequestAborted)
                : null;

            if (dynamicModels is { StatusCode: >= 200 and < 300, Body.Length: > 0 })
            {
                var response = BuildModelsResponseFromDynamicBody(dynamicModels.Body, backend, defaultModel);
                return Results.Ok(response);
            }

            var fallbackResponse = BuildConfiguredModelsResponse(configuredModels, defaultModel, backend);
            return Results.Ok(fallbackResponse);
        })
        .WithName($"Models_{SanitizeEndpointName(pattern)}")
        .WithTags("Models")
        .WithSummary("List available proxy models")
        .WithDescription($$"""
{{description}}

Example response:
{
  "object": "list",
  "defaultModel": "{{defaultModel}}",
  "backend": "{{backend}}",
  "data": [
    {
      "id": "{{defaultModel}}",
      "object": "model",
      "ownedBy": "llm-proxy",
      "created": 1710000000,
      "backend": "{{backend}}",
      "isDefault": true
    }
  ]
}
""")
        .Produces<ModelsResponse>(StatusCodes.Status200OK, "application/json");
}

void MapResponsesApiRoute(string pattern, string description)
{
    app.MapPost(pattern, async (HttpContext ctx, CancellationToken _) =>
            await HandleResponsesApiAsync(ctx))
        .WithName($"ResponsesApi_{SanitizeEndpointName(pattern)}")
        .WithTags("Codex")
        .WithSummary("OpenAI Responses API compatible endpoint")
        .WithDescription(description)
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status502BadGateway);
}

async Task HandleResponsesApiAsync(HttpContext ctx)
{
    var logger = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    var clientFactory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();

    using var reader = new StreamReader(ctx.Request.Body);
    var requestBody = await reader.ReadToEndAsync();

    logger.LogInformation("收到 Responses API 请求, 大小: {Size} bytes", requestBody.Length);

    // Codex CLI 可能发送空 body 的探测请求
    if (string.IsNullOrWhiteSpace(requestBody))
    {
        logger.LogInformation("Responses API 收到空 body 请求，返回空响应");
        ctx.Response.StatusCode = 200;
        await ctx.Response.WriteAsJsonAsync(new
        {
            id = $"resp_{Guid.NewGuid():N}",
            @object = "response",
            status = "completed",
            output = Array.Empty<object>(),
            model = defaultModel,
            usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 }
        });
        return;
    }

    JsonDocument requestDoc;
    try
    {
        requestDoc = JsonDocument.Parse(requestBody);
    }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "Responses API 请求 JSON 解析失败");
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = new { message = "Invalid JSON request body", type = "invalid_request_error" } });
        return;
    }

    using (requestDoc)
    {
        var root = requestDoc.RootElement;

        // 解析请求参数
        var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? defaultModel : defaultModel;
        var wantsStream = root.TryGetProperty("stream", out var streamEl) && streamEl.ValueKind == JsonValueKind.True;
        var instructions = root.TryGetProperty("instructions", out var instrEl) ? instrEl.GetString() : null;
        var maxTokens = root.TryGetProperty("max_output_tokens", out var maxTokEl) && maxTokEl.ValueKind == JsonValueKind.Number
            ? maxTokEl.GetInt32()
            : 16384;

        // 转换 input → Anthropic messages
        var messages = ConvertResponsesInputToMessages(root, logger);

        // 转换 tools
        var tools = ConvertResponsesToolsToAnthropic(root);

        // 构造 Anthropic Messages API 请求
        var anthropicRequest = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
            ["messages"] = messages
        };
        if (!string.IsNullOrEmpty(instructions))
        {
            anthropicRequest["system"] = instructions;
        }
        if (tools != null && tools.Count > 0)
        {
            anthropicRequest["tools"] = tools;
        }

        var anthropicRequestJson = JsonSerializer.Serialize(anthropicRequest);
        logger.LogInformation("Responses API → Anthropic Messages 转换完成, model={Model}, messages={Count}", model, messages.Count);

        if (truncationEnabled)
        {
            anthropicRequestJson = TruncateMessagesIfNeeded(anthropicRequestJson, maxRequestBodyBytes, keepRecentMessages, logger);
        }

        // 调用后端（带自动截断重试）
        var effectiveBody = anthropicRequestJson;

        async Task<ProxyResult> CallBackendForResponses(string body) =>
            backend.Equals("KiroApi", StringComparison.OrdinalIgnoreCase)
                ? await ForwardToKiroApiAsync(
                    body, kiroNodeCommand, kiroHelperPath, kiroRegion, kiroEndpoint,
                    kiroTokenFilePath, kiroRegistrationCacheDirectory,
                    kiroContentCollectionEnabled, kiroAgentMode, logger, ctx.RequestAborted)
                : await ForwardToAnthropicAsync(
                    ctx, body, upstreamBaseUrl, upstreamApiKey, maxRetries,
                    clientFactory, logger, ctx.RequestAborted);

        var result = await CallBackendForResponses(effectiveBody);

        // 如果上游过载且请求体较大，进一步截断后重试
        if (truncationEnabled && result.StatusCode is 429 or 502 or 503 or 529
            && Encoding.UTF8.GetByteCount(effectiveBody) > maxRequestBodyBytes / 2)
        {
            logger.LogWarning("Codex 请求上游返回 {StatusCode}，尝试进一步截断上下文后重试", result.StatusCode);
            var aggressiveKeep = Math.Max(keepRecentMessages / 2, 6);
            var truncatedBody = TruncateMessagesIfNeeded(effectiveBody, maxRequestBodyBytes / 2, aggressiveKeep, logger);
            if (truncatedBody != effectiveBody)
            {
                effectiveBody = truncatedBody;
                await Task.Delay(2000, ctx.RequestAborted);
                result = await CallBackendForResponses(effectiveBody);
            }
        }

        if (result.StatusCode is >= 200 and < 300)
        {
            RecordUsage(usageConnectionString, result.Body, model, backend, ctx.Request.Path, logger);
        }

        if (result.StatusCode is not (>= 200 and < 300))
        {
            ctx.Response.StatusCode = result.StatusCode;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = new { message = $"Upstream error: {result.Body}", type = "api_error", code = result.StatusCode }
            });
            return;
        }

        // 将 Anthropic 响应转换为 Responses API 格式
        if (wantsStream)
        {
            await WriteResponsesApiStreaming(ctx, result.Body, model, logger);
        }
        else
        {
            var responsesApiResponse = ConvertAnthropicToResponsesApi(result.Body, model, logger);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(responsesApiResponse);
        }
    }
}

/// <summary>
/// 将 Responses API 的 input 字段转换为 Anthropic Messages 格式
/// </summary>
static List<Dictionary<string, object?>> ConvertResponsesInputToMessages(JsonElement root, Microsoft.Extensions.Logging.ILogger logger)
{
    var messages = new List<Dictionary<string, object?>>();

    if (!root.TryGetProperty("input", out var inputEl))
    {
        return messages;
    }

    // input 可以是字符串或数组
    if (inputEl.ValueKind == JsonValueKind.String)
    {
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = inputEl.GetString()
        });
        return messages;
    }

    if (inputEl.ValueKind != JsonValueKind.Array)
    {
        return messages;
    }

    foreach (var item in inputEl.EnumerateArray())
    {
        var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
        var role = item.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null;

        // 处理 message 类型（有 role 的项）
        if (role != null || type == "message")
        {
            var effectiveRole = role ?? "user";
            // developer/system role → 跳过（已通过 instructions 处理），或映射为 user
            if (effectiveRole == "developer" || effectiveRole == "system")
            {
                // 作为 user 消息附加（instructions 更优先，但有些客户端会在 input 里放 system）
                effectiveRole = "user";
            }

            var content = item.TryGetProperty("content", out var contentEl) ? contentEl : default;
            string? textContent = null;

            if (content.ValueKind == JsonValueKind.String)
            {
                textContent = content.GetString();
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                // 提取 text 类型的内容
                var parts = new List<string>();
                foreach (var part in content.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var ptEl) ? ptEl.GetString() : null;
                    if (partType is "input_text" or "text" or "output_text")
                    {
                        var text = part.TryGetProperty("text", out var txtEl) ? txtEl.GetString() : null;
                        if (text != null) parts.Add(text);
                    }
                }
                textContent = string.Join("\n", parts);
            }

            if (!string.IsNullOrEmpty(textContent))
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = effectiveRole == "assistant" ? "assistant" : "user",
                    ["content"] = textContent
                });
            }
        }
        // 处理 function_call 类型 → 映射为 assistant 的 tool_use
        else if (type == "function_call")
        {
            var callId = item.TryGetProperty("call_id", out var cidEl) ? cidEl.GetString() ?? "" : "";
            var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            var arguments = item.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "{}" : "{}";

            object? parsedInput;
            try
            {
                parsedInput = JsonSerializer.Deserialize<object>(arguments);
            }
            catch
            {
                parsedInput = new { };
            }

            // 将 function_call 合并到上一个 assistant 消息中，或创建新的
            var toolUseBlock = new Dictionary<string, object?>
            {
                ["type"] = "tool_use",
                ["id"] = callId,
                ["name"] = name,
                ["input"] = parsedInput
            };

            // 查找最后一个 assistant 消息并追加
            var lastAssistant = messages.LastOrDefault(m => m["role"]?.ToString() == "assistant");
            if (lastAssistant != null && lastAssistant["content"] is List<object> existingBlocks)
            {
                existingBlocks.Add(toolUseBlock);
            }
            else
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = new List<object> { toolUseBlock }
                });
            }
        }
        // 处理 function_call_output 类型 → 映射为 user 的 tool_result
        else if (type == "function_call_output")
        {
            var callId = item.TryGetProperty("call_id", out var cidEl) ? cidEl.GetString() ?? "" : "";
            var output = item.TryGetProperty("output", out var outEl) ? outEl.GetString() ?? "" : "";

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = callId,
                        ["content"] = output
                    }
                }
            });
        }
    }

    return messages;
}

/// <summary>
/// 将 Responses API 的 tools 转换为 Anthropic 格式
/// </summary>
static List<Dictionary<string, object?>>? ConvertResponsesToolsToAnthropic(JsonElement root)
{
    if (!root.TryGetProperty("tools", out var toolsEl) || toolsEl.ValueKind != JsonValueKind.Array)
    {
        return null;
    }

    var tools = new List<Dictionary<string, object?>>();

    foreach (var tool in toolsEl.EnumerateArray())
    {
        var toolType = tool.TryGetProperty("type", out var ttEl) ? ttEl.GetString() : null;
        if (toolType != "function") continue; // 只支持 function 类型

        var name = tool.TryGetProperty("name", out var tnEl) ? tnEl.GetString() ?? "" : "";
        var description = tool.TryGetProperty("description", out var tdEl) ? tdEl.GetString() ?? "" : "";
        var parameters = tool.TryGetProperty("parameters", out var tpEl)
            ? JsonSerializer.Deserialize<object>(tpEl.GetRawText())
            : new { type = "object", properties = new { } };

        tools.Add(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = description,
            ["input_schema"] = parameters
        });
    }

    return tools.Count > 0 ? tools : null;
}

/// <summary>
/// 将 Anthropic Messages 响应转换为 OpenAI Responses API 格式
/// </summary>
static string ConvertAnthropicToResponsesApi(string anthropicJson, string model, Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        using var doc = JsonDocument.Parse(anthropicJson);
        var root = doc.RootElement;

        var responseId = "resp_" + Guid.NewGuid().ToString("N")[..24];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var output = new List<object>();

        if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentEl.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var btEl) ? btEl.GetString() : null;

                if (blockType == "text")
                {
                    var text = block.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? "" : "";
                    output.Add(new Dictionary<string, object?>
                    {
                        ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..24],
                        ["type"] = "message",
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new List<object>
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "output_text",
                                ["text"] = text,
                                ["annotations"] = Array.Empty<object>()
                            }
                        }
                    });
                }
                else if (blockType == "tool_use")
                {
                    var toolId = block.TryGetProperty("id", out var tidEl) ? tidEl.GetString() ?? "" : "";
                    var toolName = block.TryGetProperty("name", out var tnEl) ? tnEl.GetString() ?? "" : "";
                    var toolInput = block.TryGetProperty("input", out var tiEl) ? tiEl.GetRawText() : "{}";

                    output.Add(new Dictionary<string, object?>
                    {
                        ["id"] = toolId,
                        ["type"] = "function_call",
                        ["status"] = "completed",
                        ["call_id"] = toolId,
                        ["name"] = toolName,
                        ["arguments"] = toolInput
                    });
                }
            }
        }

        // 解析 usage
        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usageEl))
        {
            if (usageEl.TryGetProperty("input_tokens", out var itEl) && itEl.ValueKind == JsonValueKind.Number)
                inputTokens = itEl.GetInt32();
            if (usageEl.TryGetProperty("output_tokens", out var otEl) && otEl.ValueKind == JsonValueKind.Number)
                outputTokens = otEl.GetInt32();
        }

        var stopReason = root.TryGetProperty("stop_reason", out var srEl) ? srEl.GetString() : "end_turn";
        var status = stopReason == "tool_use" ? "incomplete" : "completed";

        var response = new Dictionary<string, object?>
        {
            ["id"] = responseId,
            ["object"] = "response",
            ["created_at"] = created,
            ["status"] = status,
            ["model"] = model,
            ["output"] = output,
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens
            }
        };

        return JsonSerializer.Serialize(response);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Anthropic → Responses API 转换失败");
        return JsonSerializer.Serialize(new
        {
            id = "resp_error",
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "failed",
            model,
            output = Array.Empty<object>(),
            error = new { message = ex.Message, type = "server_error" }
        });
    }
}

/// <summary>
/// 以 OpenAI Responses API SSE 流式格式输出响应
/// </summary>
static async Task WriteResponsesApiStreaming(HttpContext ctx, string anthropicJson, string model, Microsoft.Extensions.Logging.ILogger logger)
{
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";

    try
    {
        using var doc = JsonDocument.Parse(anthropicJson);
        var root = doc.RootElement;

        var responseId = "resp_" + Guid.NewGuid().ToString("N")[..24];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 解析 usage
        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usageEl))
        {
            if (usageEl.TryGetProperty("input_tokens", out var itEl) && itEl.ValueKind == JsonValueKind.Number)
                inputTokens = itEl.GetInt32();
            if (usageEl.TryGetProperty("output_tokens", out var otEl) && otEl.ValueKind == JsonValueKind.Number)
                outputTokens = otEl.GetInt32();
        }

        var stopReason = root.TryGetProperty("stop_reason", out var srEl) ? srEl.GetString() : "end_turn";

        // 1. response.created
        var createdEvent = new Dictionary<string, object?>
        {
            ["type"] = "response.created",
            ["response"] = new Dictionary<string, object?>
            {
                ["id"] = responseId,
                ["object"] = "response",
                ["created_at"] = created,
                ["status"] = "in_progress",
                ["model"] = model,
                ["output"] = Array.Empty<object>()
            }
        };
        await WriteResponsesSseEvent(ctx, createdEvent);

        // 2. response.in_progress
        await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
        {
            ["type"] = "response.in_progress",
            ["response"] = new Dictionary<string, object?>
            {
                ["id"] = responseId,
                ["object"] = "response",
                ["created_at"] = created,
                ["status"] = "in_progress",
                ["model"] = model,
                ["output"] = Array.Empty<object>()
            }
        });

        // 3. 遍历 content blocks
        if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            var outputIndex = 0;
            foreach (var block in contentEl.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var btEl) ? btEl.GetString() : null;

                if (blockType == "text")
                {
                    var text = block.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? "" : "";
                    var itemId = "msg_" + Guid.NewGuid().ToString("N")[..24];

                    // response.output_item.added
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.output_item.added",
                        ["output_index"] = outputIndex,
                        ["item"] = new Dictionary<string, object?>
                        {
                            ["id"] = itemId,
                            ["type"] = "message",
                            ["status"] = "in_progress",
                            ["role"] = "assistant",
                            ["content"] = Array.Empty<object>()
                        }
                    });

                    // response.content_part.added
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.content_part.added",
                        ["item_id"] = itemId,
                        ["output_index"] = outputIndex,
                        ["content_index"] = 0,
                        ["part"] = new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = "",
                            ["annotations"] = Array.Empty<object>()
                        }
                    });

                    // response.output_text.delta — 分块输出
                    var chunkSize = 80;
                    for (var i = 0; i < text.Length; i += chunkSize)
                    {
                        var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i));
                        await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                        {
                            ["type"] = "response.output_text.delta",
                            ["item_id"] = itemId,
                            ["output_index"] = outputIndex,
                            ["content_index"] = 0,
                            ["delta"] = chunk
                        });
                    }

                    // response.output_text.done
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.output_text.done",
                        ["item_id"] = itemId,
                        ["output_index"] = outputIndex,
                        ["content_index"] = 0,
                        ["text"] = text
                    });

                    // response.content_part.done
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.content_part.done",
                        ["item_id"] = itemId,
                        ["output_index"] = outputIndex,
                        ["content_index"] = 0,
                        ["part"] = new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = text,
                            ["annotations"] = Array.Empty<object>()
                        }
                    });

                    // response.output_item.done
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.output_item.done",
                        ["output_index"] = outputIndex,
                        ["item"] = new Dictionary<string, object?>
                        {
                            ["id"] = itemId,
                            ["type"] = "message",
                            ["status"] = "completed",
                            ["role"] = "assistant",
                            ["content"] = new List<object>
                            {
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "output_text",
                                    ["text"] = text,
                                    ["annotations"] = Array.Empty<object>()
                                }
                            }
                        }
                    });

                    outputIndex++;
                }
                else if (blockType == "tool_use")
                {
                    var toolId = block.TryGetProperty("id", out var tidEl) ? tidEl.GetString() ?? "" : "";
                    var toolName = block.TryGetProperty("name", out var tnEl) ? tnEl.GetString() ?? "" : "";
                    var toolInput = block.TryGetProperty("input", out var tiEl) ? tiEl.GetRawText() : "{}";

                    // response.output_item.added
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.output_item.added",
                        ["output_index"] = outputIndex,
                        ["item"] = new Dictionary<string, object?>
                        {
                            ["id"] = toolId,
                            ["type"] = "function_call",
                            ["status"] = "in_progress",
                            ["call_id"] = toolId,
                            ["name"] = toolName,
                            ["arguments"] = ""
                        }
                    });

                    // response.function_call_arguments.delta
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.function_call_arguments.delta",
                        ["item_id"] = toolId,
                        ["output_index"] = outputIndex,
                        ["delta"] = toolInput
                    });

                    // response.function_call_arguments.done
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.function_call_arguments.done",
                        ["item_id"] = toolId,
                        ["output_index"] = outputIndex,
                        ["arguments"] = toolInput
                    });

                    // response.output_item.done
                    await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
                    {
                        ["type"] = "response.output_item.done",
                        ["output_index"] = outputIndex,
                        ["item"] = new Dictionary<string, object?>
                        {
                            ["id"] = toolId,
                            ["type"] = "function_call",
                            ["status"] = "completed",
                            ["call_id"] = toolId,
                            ["name"] = toolName,
                            ["arguments"] = toolInput
                        }
                    });

                    outputIndex++;
                }
            }
        }

        // 4. response.completed
        var finalStatus = stopReason == "tool_use" ? "incomplete" : "completed";
        await WriteResponsesSseEvent(ctx, new Dictionary<string, object?>
        {
            ["type"] = "response.completed",
            ["response"] = new Dictionary<string, object?>
            {
                ["id"] = responseId,
                ["object"] = "response",
                ["created_at"] = created,
                ["status"] = finalStatus,
                ["model"] = model,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = inputTokens,
                    ["output_tokens"] = outputTokens,
                    ["total_tokens"] = inputTokens + outputTokens
                }
            }
        });

        await ctx.Response.Body.FlushAsync();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Responses API SSE streaming 转换失败");
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = ex.Message, type = "server_error" } });
        }
    }
}

static async Task WriteResponsesSseEvent(HttpContext ctx, object data)
{
    var json = JsonSerializer.Serialize(data);
    await ctx.Response.WriteAsync($"data: {json}\n\n");
    await ctx.Response.Body.FlushAsync();
}

async Task HandleClaudeMessagesAsync(HttpContext ctx)
{
    var logger = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    var clientFactory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();

    using var reader = new StreamReader(ctx.Request.Body);
    var requestBody = await reader.ReadToEndAsync();

    logger.LogInformation("收到代理请求, 大小: {Size} bytes, 后端: {Backend}", requestBody.Length, backend);

    ClaudeMessagesRequest requestModel;
    try
    {
        requestModel = ParseClaudeMessagesRequest(requestBody);
    }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "请求 JSON 解析失败");
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid JSON request body" });
        return;
    }

    // 无论 stream=true 还是 false，都先用非流式方式获取完整响应
    // 对于 stream=true 的请求，拿到完整响应后转成 SSE 格式输出
    var wantsStream = requestModel.Stream;

    // 构造非流式请求体（强制 stream=false 发给后端）
    var backendRequestBody = requestBody;
    if (wantsStream)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var dict = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "stream")
                    dict[prop.Name] = false;
                else
                    dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
            }
            backendRequestBody = JsonSerializer.Serialize(dict);
        }
        catch
        {
            // 解析失败就用原始请求体
        }
    }

    var cacheScope = $"{backend}|{upstreamBaseUrl}|{kiroNodeCommand}|{kiroEndpoint}|{kiroAgentMode}";
    if (cacheEnabled)
    {
        var cached = TryReadCache(backendRequestBody, cacheScope, cacheDir, cacheTtlMinutes, logger);
        if (cached != null)
        {
            logger.LogInformation("缓存命中，直接返回");
            if (wantsStream)
            {
                await WriteStreamingResponse(ctx, cached, logger);
            }
            else
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(cached);
            }
            return;
        }
    }

    ProxyResult result = backend.Equals("KiroApi", StringComparison.OrdinalIgnoreCase)
        ? await ForwardToKiroApiAsync(
            backendRequestBody,
            kiroNodeCommand,
            kiroHelperPath,
            kiroRegion,
            kiroEndpoint,
            kiroTokenFilePath,
            kiroRegistrationCacheDirectory,
            kiroContentCollectionEnabled,
            kiroAgentMode,
            logger,
            ctx.RequestAborted)
        : await ForwardToAnthropicAsync(
            ctx,
            backendRequestBody,
            upstreamBaseUrl,
            upstreamApiKey,
            maxRetries,
            clientFactory,
            logger,
            ctx.RequestAborted);

    if (cacheEnabled && result.StatusCode is >= 200 and < 300 && !result.Body.Contains("\"type\":\"tool_use\""))
    {
        WriteCache(backendRequestBody, cacheScope, result.Body, cacheDir, maxSizeMB, cacheTtlMinutes, logger);
    }

    if (result.StatusCode is >= 200 and < 300)
    {
        RecordUsage(usageConnectionString, result.Body, requestModel.Model, backend, ctx.Request.Path, logger);
    }

    if (wantsStream && result.StatusCode is >= 200 and < 300)
    {
        await WriteStreamingResponse(ctx, result.Body, logger);
    }
    else
    {
        ctx.Response.StatusCode = result.StatusCode;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(result.Body);
    }
}

/// <summary>
/// 将完整的 Claude JSON 响应转换为 Anthropic SSE streaming 格式输出
/// 遵循 Anthropic Messages Streaming 协议：
/// https://docs.anthropic.com/en/api/messages-streaming
/// </summary>
static async Task WriteStreamingResponse(HttpContext ctx, string jsonBody, Microsoft.Extensions.Logging.ILogger logger)
{
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";

    try
    {
        using var doc = JsonDocument.Parse(jsonBody);
        var root = doc.RootElement;

        var msgId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "msg_proxy" : "msg_proxy";
        var model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? "kiro" : "kiro";
        var stopReason = root.TryGetProperty("stop_reason", out var stopEl) ? stopEl.GetString() : "end_turn";
        var usage = root.TryGetProperty("usage", out var usageEl) ? usageEl : default;
        var inputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens", out var itEl) ? itEl.GetInt32() : 0;
        var outputTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens", out var otEl) ? otEl.GetInt32() : 0;

        // 1. message_start 事件
        var messageStart = new
        {
            type = "message_start",
            message = new
            {
                id = msgId,
                type = "message",
                role = "assistant",
                content = Array.Empty<object>(),
                model,
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = inputTokens, output_tokens = 0 }
            }
        };
        await WriteSseEvent(ctx, "message_start", JsonSerializer.Serialize(messageStart));

        // 2. 遍历 content 数组，为每个 block 生成 content_block_start / delta / stop
        if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var block in contentEl.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var btEl) ? btEl.GetString() : "text";

                if (blockType == "text")
                {
                    var text = block.TryGetProperty("text", out var txtEl) ? txtEl.GetString() ?? "" : "";

                    // content_block_start
                    await WriteSseEvent(ctx, "content_block_start", JsonSerializer.Serialize(new
                    {
                        type = "content_block_start",
                        index,
                        content_block = new { type = "text", text = "" }
                    }));

                    // 分块输出 text delta（每 80 字符一个 chunk，模拟流式效果）
                    var chunkSize = 80;
                    for (var i = 0; i < text.Length; i += chunkSize)
                    {
                        var chunk = text.Substring(i, Math.Min(chunkSize, text.Length - i));
                        await WriteSseEvent(ctx, "content_block_delta", JsonSerializer.Serialize(new
                        {
                            type = "content_block_delta",
                            index,
                            delta = new { type = "text_delta", text = chunk }
                        }));
                    }

                    // content_block_stop
                    await WriteSseEvent(ctx, "content_block_stop", JsonSerializer.Serialize(new
                    {
                        type = "content_block_stop",
                        index
                    }));
                }
                else if (blockType == "tool_use")
                {
                    var toolId = block.TryGetProperty("id", out var tidEl) ? tidEl.GetString() ?? "" : "";
                    var toolName = block.TryGetProperty("name", out var tnEl) ? tnEl.GetString() ?? "" : "";
                    var toolInput = block.TryGetProperty("input", out var tiEl) ? tiEl.GetRawText() : "{}";

                    // content_block_start
                    await WriteSseEvent(ctx, "content_block_start", JsonSerializer.Serialize(new
                    {
                        type = "content_block_start",
                        index,
                        content_block = new { type = "tool_use", id = toolId, name = toolName, input = new { } }
                    }));

                    // input_json_delta
                    await WriteSseEvent(ctx, "content_block_delta", JsonSerializer.Serialize(new
                    {
                        type = "content_block_delta",
                        index,
                        delta = new { type = "input_json_delta", partial_json = toolInput }
                    }));

                    // content_block_stop
                    await WriteSseEvent(ctx, "content_block_stop", JsonSerializer.Serialize(new
                    {
                        type = "content_block_stop",
                        index
                    }));
                }

                index++;
            }
        }

        // 3. message_delta 事件
        await WriteSseEvent(ctx, "message_delta", JsonSerializer.Serialize(new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = outputTokens }
        }));

        // 4. message_stop 事件
        await WriteSseEvent(ctx, "message_stop", JsonSerializer.Serialize(new { type = "message_stop" }));

        await ctx.Response.Body.FlushAsync();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "SSE streaming 转换失败");
        // 如果还没开始写 header，返回错误；否则只能中断流
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 502;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { error = "Streaming conversion failed", detail = ex.Message });
        }
    }
}

/// <summary>
/// 写入单个 SSE 事件
/// </summary>
static async Task WriteSseEvent(HttpContext ctx, string eventType, string data)
{
    await ctx.Response.WriteAsync($"event: {eventType}\ndata: {data}\n\n");
    await ctx.Response.Body.FlushAsync();
}

static async Task<ProxyResult> ForwardToAnthropicAsync(
    HttpContext ctx,
    string requestBody,
    string upstreamBaseUrl,
    string upstreamApiKey,
    int maxRetries,
    IHttpClientFactory clientFactory,
    Microsoft.Extensions.Logging.ILogger logger,
    CancellationToken cancellationToken)
{
    var clientApiKey = ctx.Request.Headers["x-api-key"].FirstOrDefault() ?? "";
    var anthropicVersion = ctx.Request.Headers["anthropic-version"].FirstOrDefault() ?? "2023-06-01";
    var effectiveApiKey = !string.IsNullOrEmpty(upstreamApiKey) ? upstreamApiKey : clientApiKey;

    if (string.IsNullOrEmpty(effectiveApiKey))
    {
        logger.LogError("无可用 API Key：配置和客户端均未提供");
        return ProxyResult.Json(401, new { error = "No API key available" });
    }

    var upstreamUrl = $"{upstreamBaseUrl.TrimEnd('/')}/v1/messages";
    logger.LogInformation("转发到 HTTP 上游: {Url}", upstreamUrl);

    string? responseBody = null;
    int? statusCode = null;

    for (var attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            var httpClient = clientFactory.CreateClient("upstream");
            using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", effectiveApiKey);
            request.Headers.Add("anthropic-version", anthropicVersion);

            var response = await httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            statusCode = (int)response.StatusCode;

            logger.LogInformation("上游响应: {StatusCode}, 大小: {Size} bytes", statusCode, responseBody.Length);

            if (response.IsSuccessStatusCode)
            {
                return new ProxyResult(statusCode.Value, responseBody);
            }

            if ((statusCode == 429 || statusCode == 502 || statusCode == 503 || statusCode == 529) &&
                attempt < maxRetries)
            {
                var delay = attempt * 3000;
                if (statusCode == 429 &&
                    response.Headers.TryGetValues("retry-after", out var retryValues) &&
                    int.TryParse(retryValues.FirstOrDefault(), out var retryAfter))
                {
                    delay = retryAfter * 1000;
                }

                logger.LogWarning("上游返回 {StatusCode}，第 {Attempt} 次重试，等待 {Delay}ms", statusCode, attempt, delay);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            break;
        }
        catch (HttpRequestException ex) when (attempt < maxRetries)
        {
            logger.LogWarning(ex, "请求上游失败，第 {Attempt} 次重试", attempt);
            await Task.Delay(attempt * 3000, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < maxRetries)
        {
            logger.LogWarning(ex, "请求上游超时，第 {Attempt} 次重试", attempt);
            await Task.Delay(attempt * 3000, cancellationToken);
        }
    }

    return new ProxyResult(
        statusCode ?? 502,
        responseBody ?? "{\"error\":\"upstream request failed after retries\"}");
}

static async Task<ProxyResult> ForwardToKiroApiAsync(
    string requestBody,
    string nodeCommand,
    string helperPath,
    string region,
    string endpoint,
    string tokenFilePath,
    string registrationCacheDirectory,
    bool contentCollectionEnabled,
    string agentMode,
    Microsoft.Extensions.Logging.ILogger logger,
    CancellationToken cancellationToken)
{
    if (!File.Exists(helperPath))
    {
        return ProxyResult.Json(500, new
        {
            error = "Kiro API helper script not found",
            helperPath
        });
    }

    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodeCommand,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(helperPath);
        startInfo.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "0";

        using var process = new Process
        {
            StartInfo = startInfo
        };

        logger.LogInformation("调用 Kiro API helper: {NodeCommand} {HelperPath}", nodeCommand, helperPath);
        process.Start();

        var payload = JsonSerializer.Serialize(new
        {
            requestBody,
            options = new
            {
                region,
                endpoint,
                tokenFilePath,
                registrationCacheDirectory,
                contentCollectionEnabled,
                agentMode
            }
        });

        await process.StandardInput.WriteAsync(payload);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogWarning("Kiro API helper 无输出, stderr: {Stderr}", stderr);
            return ProxyResult.Json(502, new
            {
                error = "Kiro API helper returned no output",
                detail = stderr
            });
        }

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var statusCode = root.TryGetProperty("statusCode", out var statusElement) && statusElement.TryGetInt32(out var parsedStatus)
            ? parsedStatus
            : process.ExitCode == 0 ? 200 : 502;
        var body = root.TryGetProperty("body", out var bodyElement)
            ? bodyElement.GetString() ?? "{\"error\":\"Invalid helper response\"}"
            : "{\"error\":\"Invalid helper response\"}";

        if (process.ExitCode != 0)
        {
            logger.LogWarning("Kiro API helper 退出异常, ExitCode: {ExitCode}, stderr: {Stderr}, body: {Body}", process.ExitCode, stderr, body);
        }
        else
        {
            logger.LogInformation("Kiro API helper 响应成功, 大小: {Size} bytes", body.Length);
        }

        return new ProxyResult(statusCode, body);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        logger.LogWarning("Kiro API helper 调用被取消");
        return ProxyResult.Json(499, new { error = "Request cancelled" });
    }
    catch (Win32Exception ex)
    {
        logger.LogWarning(ex, "Node 命令不可用");
        return ProxyResult.Json(503, new
        {
            error = "Node runtime not found",
            nodeCommand
        });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "调用 Kiro API helper 失败");
        return ProxyResult.Json(502, new
        {
            error = "Kiro API helper failed",
            detail = ex.Message
        });
    }
}

static async Task<ProxyResult?> ForwardToKiroModelsAsync(
    string nodeCommand,
    string helperPath,
    string region,
    string endpoint,
    string tokenFilePath,
    string registrationCacheDirectory,
    bool contentCollectionEnabled,
    string agentMode,
    Microsoft.Extensions.Logging.ILogger logger,
    CancellationToken cancellationToken)
{
    if (!File.Exists(helperPath))
    {
        return null;
    }

    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodeCommand,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(helperPath);
        startInfo.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "0";

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        var payload = JsonSerializer.Serialize(new
        {
            action = "listModels",
            options = new
            {
                region,
                endpoint,
                tokenFilePath,
                registrationCacheDirectory,
                contentCollectionEnabled,
                agentMode
            }
        });

        await process.StandardInput.WriteAsync(payload);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogWarning("Kiro models helper 无输出, stderr: {Stderr}", stderr);
            return null;
        }

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var statusCode = root.TryGetProperty("statusCode", out var statusElement) && statusElement.TryGetInt32(out var parsedStatus)
            ? parsedStatus
            : process.ExitCode == 0 ? 200 : 502;
        var body = root.TryGetProperty("body", out var bodyElement)
            ? bodyElement.GetString() ?? string.Empty
            : string.Empty;

        if (process.ExitCode != 0)
        {
            logger.LogWarning("Kiro models helper 退出异常, ExitCode: {ExitCode}, stderr: {Stderr}", process.ExitCode, stderr);
        }

        return new ProxyResult(statusCode, body);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex, "动态探测 Kiro 模型失败，将回退到静态配置");
        return null;
    }
}

static async Task<TokenRefreshResult> RefreshKiroTokenAsync(
    string nodeCommand,
    string helperPath,
    string region,
    string endpoint,
    string tokenFilePath,
    string registrationCacheDirectory,
    bool contentCollectionEnabled,
    string agentMode)
{
    // 先检查 token 是否真的需要刷新（5 分钟内过期才刷新）
    if (File.Exists(tokenFilePath))
    {
        try
        {
            var tokenJson = await File.ReadAllTextAsync(tokenFilePath);
            using var tokenDoc = JsonDocument.Parse(tokenJson);
            if (tokenDoc.RootElement.TryGetProperty("expiresAt", out var expiresAtEl))
            {
                var expiresAtStr = expiresAtEl.GetString();
                if (DateTime.TryParse(expiresAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiresAt))
                {
                    if (expiresAt > DateTime.UtcNow.AddMinutes(5))
                    {
                        return new TokenRefreshResult(true, expiresAtStr, null);
                    }
                }
            }
        }
        catch { /* 解析失败则继续尝试刷新 */ }
    }

    // 用一个轻量级请求触发 node helper 内部的 refreshTokenIfNeeded
    if (!File.Exists(helperPath))
    {
        return new TokenRefreshResult(false, null, "Helper script not found");
    }

    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodeCommand,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(helperPath);
        startInfo.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "0";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // 使用 listModels action 来触发 token refresh（它内部会先调 refreshTokenIfNeeded）
        var payload = JsonSerializer.Serialize(new
        {
            action = "listModels",
            options = new
            {
                region,
                endpoint,
                tokenFilePath,
                registrationCacheDirectory,
                contentCollectionEnabled,
                agentMode
            }
        });

        await process.StandardInput.WriteAsync(payload);
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            return new TokenRefreshResult(false, null, $"Helper exit code {process.ExitCode}: {stderr}");
        }

        // 刷新后读取新的过期时间
        if (File.Exists(tokenFilePath))
        {
            var newTokenJson = await File.ReadAllTextAsync(tokenFilePath);
            using var newTokenDoc = JsonDocument.Parse(newTokenJson);
            var newExpiresAt = newTokenDoc.RootElement.TryGetProperty("expiresAt", out var newEl)
                ? newEl.GetString()
                : "unknown";
            return new TokenRefreshResult(true, newExpiresAt, null);
        }

        return new TokenRefreshResult(true, "unknown", null);
    }
    catch (Exception ex)
    {
        return new TokenRefreshResult(false, null, ex.Message);
    }
}

/// <summary>
/// 当请求体超过 maxBytes 时，裁剪中间的旧消息，保留 system + 前2条 + 最近 keepRecent 条消息。
/// 这样可以避免因对话太长导致上游返回 "high demand" / 529 / 503 等错误。
/// </summary>
static string TruncateMessagesIfNeeded(string requestBody, int maxBytes, int keepRecent, Microsoft.Extensions.Logging.ILogger logger)
{
    if (Encoding.UTF8.GetByteCount(requestBody) <= maxBytes)
    {
        return requestBody;
    }

    try
    {
        using var doc = JsonDocument.Parse(requestBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("messages", out var messagesEl) || messagesEl.ValueKind != JsonValueKind.Array)
        {
            return requestBody;
        }

        var messages = new List<JsonElement>();
        foreach (var msg in messagesEl.EnumerateArray())
        {
            messages.Add(msg.Clone());
        }

        if (messages.Count <= keepRecent + 2)
        {
            return requestBody;
        }

        var keepFirst = 2;
        var totalToKeep = keepFirst + keepRecent;
        var trimmed = messages.Count;

        while (messages.Count > totalToKeep && Encoding.UTF8.GetByteCount(RebuildRequestJson(root, messages)) > maxBytes)
        {
            messages.RemoveAt(keepFirst);
        }

        if (messages.Count < trimmed)
        {
            logger.LogWarning(
                "上下文截断: 原始 {OriginalCount} 条消息裁剪为 {TrimmedCount} 条 (保留前{KeepFirst}条+最近{KeepRecent}条), 原始大小: {OriginalSize} bytes",
                trimmed, messages.Count, keepFirst, keepRecent, Encoding.UTF8.GetByteCount(requestBody));

            var result = RebuildRequestJson(root, messages);
            logger.LogInformation("截断后请求体大小: {Size} bytes", Encoding.UTF8.GetByteCount(result));
            return result;
        }

        return requestBody;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "上下文截断处理失败，使用原始请求体");
        return requestBody;
    }
}

/// <summary>
/// 用裁剪后的 messages 列表重新构建请求 JSON，保留其他所有字段不变。
/// </summary>
static string RebuildRequestJson(JsonElement originalRoot, List<JsonElement> newMessages)
{
    var dict = new Dictionary<string, object?>();
    foreach (var prop in originalRoot.EnumerateObject())
    {
        if (prop.Name == "messages")
        {
            dict["messages"] = newMessages.Select(m => JsonSerializer.Deserialize<object>(m.GetRawText())).ToList();
        }
        else
        {
            dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }
    }
    return JsonSerializer.Serialize(dict);
}
static ClaudeMessagesRequest ParseClaudeMessagesRequest(string requestBody)
{
    using var document = JsonDocument.Parse(requestBody);
    var root = document.RootElement;

    var model = root.TryGetProperty("model", out var modelElement)
        ? modelElement.GetString() ?? "kiro"
        : "kiro";
    var stream = root.TryGetProperty("stream", out var streamElement) &&
                 streamElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                 streamElement.GetBoolean();

    return new ClaudeMessagesRequest(model, stream);
}

static ModelsResponse BuildConfiguredModelsResponse(IEnumerable<string> models, string defaultModel, string backend)
{
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    return new ModelsResponse(
        "list",
        models.Select(model => new ModelInfo(
            model,
            "model",
            "llm-proxy",
            created,
            backend,
            model == defaultModel,
            null,
            null,
            null,
            null,
            "configured")).ToArray(),
        defaultModel,
        backend,
        "configured");
}

static ModelsResponse BuildModelsResponseFromDynamicBody(string body, string backend, string fallbackDefaultModel)
{
    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var discoveredDefaultModel = root.TryGetProperty("defaultModel", out var defaultModelElement) &&
                                 defaultModelElement.ValueKind == JsonValueKind.Object &&
                                 defaultModelElement.TryGetProperty("id", out var defaultIdElement)
        ? defaultIdElement.GetString()
        : null;

    var models = new List<ModelInfo>();
    if (root.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array)
    {
        foreach (var model in modelsElement.EnumerateArray())
        {
            var id = model.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            models.Add(new ModelInfo(
                id,
                "model",
                "kiro",
                created,
                backend,
                string.Equals(id, discoveredDefaultModel, StringComparison.OrdinalIgnoreCase),
                model.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
                model.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() : null,
                model.TryGetProperty("rateUnit", out var rateUnitElement) ? rateUnitElement.GetString() : null,
                model.TryGetProperty("maxInputTokens", out var maxTokensElement) && maxTokensElement.ValueKind == JsonValueKind.Number
                    ? maxTokensElement.GetInt32()
                    : null,
                "dynamic"));
        }
    }

    var effectiveDefault = discoveredDefaultModel
        ?? models.FirstOrDefault(model => model.IsDefault)?.Id
        ?? fallbackDefaultModel;

    return new ModelsResponse(
        "list",
        models.ToArray(),
        effectiveDefault,
        backend,
        "dynamic");
}

static string SanitizeEndpointName(string pattern)
{
    var builder = new StringBuilder(pattern.Length);
    foreach (var ch in pattern)
    {
        builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
    }

    return builder.ToString().Trim('_');
}

static string GetCacheKey(string cacheScope, string requestBody)
{
    var combined = $"{cacheScope}\n{requestBody}";
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static string? TryReadCache(
    string requestBody,
    string cacheScope,
    string cacheDir,
    int ttlMinutes,
    Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        var key = GetCacheKey(cacheScope, requestBody);
        var path = Path.Combine(cacheDir, $"{key}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if ((DateTime.UtcNow - info.LastWriteTimeUtc).TotalMinutes > ttlMinutes)
        {
            File.Delete(path);
            return null;
        }

        return File.ReadAllText(path);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "读取缓存失败");
        return null;
    }
}

static void WriteCache(
    string requestBody,
    string cacheScope,
    string responseBody,
    string cacheDir,
    int maxSizeMB,
    int ttlMinutes,
    Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        var key = GetCacheKey(cacheScope, requestBody);
        var path = Path.Combine(cacheDir, $"{key}.json");
        File.WriteAllText(path, responseBody);

        _ = Task.Run(() =>
        {
            try
            {
                var files = Directory.GetFiles(cacheDir, "*.json");
                var totalSize = files.Sum(f => new FileInfo(f).Length);

                if (totalSize > (long)maxSizeMB * 1024 * 1024)
                {
                    var ordered = files.Select(f => new FileInfo(f))
                        .OrderBy(f => f.LastWriteTimeUtc)
                        .ToList();

                    foreach (var file in ordered)
                    {
                        file.Delete();
                        totalSize -= file.Length;
                        if (totalSize <= (long)(maxSizeMB * 1024 * 1024 * 0.8))
                        {
                            break;
                        }
                    }
                }

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    if (info.Exists && (DateTime.UtcNow - info.LastWriteTimeUtc).TotalMinutes > ttlMinutes * 2)
                    {
                        info.Delete();
                    }
                }
            }
            catch
            {
                // 清理失败不影响主流程
            }
        });
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "写入缓存失败");
    }
}

static void InitUsageDb(string connectionString)
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS token_usage (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            model TEXT NOT NULL,
            backend TEXT NOT NULL,
            route TEXT NOT NULL,
            input_tokens INTEGER NOT NULL DEFAULT 0,
            output_tokens INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_usage_timestamp ON token_usage(timestamp);
        CREATE INDEX IF NOT EXISTS idx_usage_model ON token_usage(model);
        """;
    cmd.ExecuteNonQuery();
    Log.Information("Token 用量数据库已初始化: {Path}", connectionString);
}

static void RecordUsage(
    string connectionString,
    string responseBody,
    string model,
    string backend,
    string route,
    Microsoft.Extensions.Logging.ILogger logger)
{
    try
    {
        int inputTokens = 0, outputTokens = 0;
        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it))
            {
                inputTokens = it.ValueKind == JsonValueKind.Number ? it.GetInt32() : 0;
            }
            if (usage.TryGetProperty("output_tokens", out var ot))
            {
                outputTokens = ot.ValueKind == JsonValueKind.Number ? ot.GetInt32() : 0;
            }
        }
        else
        {
            logger.LogWarning("响应中未找到 usage 字段, responseBody 前200字符: {Preview}",
                responseBody.Length > 200 ? responseBody[..200] : responseBody);
        }

        if (inputTokens == 0 && outputTokens == 0)
        {
            logger.LogWarning("Token 用量为 0, model={Model}, responseBody 前300字符: {Preview}",
                model, responseBody.Length > 300 ? responseBody[..300] : responseBody);
        }

        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = new SqliteCommand(
            "INSERT INTO token_usage (timestamp, model, backend, route, input_tokens, output_tokens) VALUES (@ts, @model, @backend, @route, @input, @output)",
            conn);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@model", model);
        cmd.Parameters.AddWithValue("@backend", backend);
        cmd.Parameters.AddWithValue("@route", route);
        cmd.Parameters.AddWithValue("@input", inputTokens);
        cmd.Parameters.AddWithValue("@output", outputTokens);
        cmd.ExecuteNonQuery();

        logger.LogInformation("记录 token 用量: model={Model}, input={Input}, output={Output}", model, inputTokens, outputTokens);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "记录 token 用量失败, responseBody 前200字符: {Preview}",
            responseBody?.Length > 200 ? responseBody[..200] : responseBody);
    }
}

static string GetUsageDashboardHtml() => """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Token 用量统计 - LlmProxy</title>
<script src="https://cdn.jsdelivr.net/npm/chart.js@4"></script>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background: #f5f7fa; color: #333; }
  .header { background: #1a1a2e; color: #fff; padding: 20px 32px; }
  .header h1 { font-size: 20px; font-weight: 600; }
  .container { max-width: 1200px; margin: 0 auto; padding: 24px; }
  .controls { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; margin-bottom: 24px; }
  .controls select, .controls input, .controls button {
    padding: 8px 14px; border: 1px solid #d1d5db; border-radius: 6px; font-size: 14px; background: #fff;
  }
  .controls button { background: #1a1a2e; color: #fff; border: none; cursor: pointer; }
  .controls button:hover { background: #16213e; }
  .summary { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 16px; margin-bottom: 24px; }
  .card { background: #fff; border-radius: 10px; padding: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); }
  .card .label { font-size: 13px; color: #6b7280; margin-bottom: 4px; }
  .card .value { font-size: 28px; font-weight: 700; color: #1a1a2e; }
  .card .sub { font-size: 12px; color: #9ca3af; margin-top: 2px; }
  .chart-wrap { background: #fff; border-radius: 10px; padding: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); margin-bottom: 24px; }
  .chart-wrap h3 { font-size: 15px; margin-bottom: 12px; color: #374151; }
  table { width: 100%; border-collapse: collapse; font-size: 14px; }
  th, td { padding: 10px 14px; text-align: left; border-bottom: 1px solid #e5e7eb; }
  th { background: #f9fafb; font-weight: 600; color: #6b7280; font-size: 12px; text-transform: uppercase; }
  tr:hover td { background: #f9fafb; }
  .empty { text-align: center; padding: 40px; color: #9ca3af; }
</style>
</head>
<body>
<div class="header"><h1>Token 用量统计</h1></div>
<div class="container">
  <div class="controls">
    <select id="granularity">
      <option value="hour">按小时</option>
      <option value="day" selected>按天</option>
      <option value="week">按周</option>
      <option value="month">按月</option>
    </select>
    <input type="date" id="startDate">
    <span>至</span>
    <input type="date" id="endDate">
    <select id="modelFilter"><option value="">全部模型</option></select>
    <button onclick="loadData()">查询</button>
  </div>
  <div class="summary">
    <div class="card"><div class="label">总请求数</div><div class="value" id="totalRequests">-</div></div>
    <div class="card"><div class="label">输入 Tokens</div><div class="value" id="totalInput">-</div><div class="sub" id="avgInput"></div></div>
    <div class="card"><div class="label">输出 Tokens</div><div class="value" id="totalOutput">-</div><div class="sub" id="avgOutput"></div></div>
    <div class="card"><div class="label">总 Tokens</div><div class="value" id="totalTokens">-</div><div class="sub" id="avgTotal"></div></div>
  </div>
  <div class="chart-wrap"><h3>Token 用量趋势</h3><canvas id="trendChart" height="100"></canvas></div>
  <div class="chart-wrap"><h3>模型用量分布</h3><canvas id="modelChart" height="80"></canvas></div>
  <div class="chart-wrap">
    <h3>明细数据</h3>
    <table>
      <thead><tr><th>时段</th><th>模型</th><th>请求数</th><th>输入</th><th>输出</th><th>合计</th></tr></thead>
      <tbody id="tableBody"><tr><td colspan="6" class="empty">点击查询加载数据</td></tr></tbody>
    </table>
  </div>
</div>
<script>
let trendChart, modelChart;
const fmt = n => n >= 1e6 ? (n/1e6).toFixed(1)+'M' : n >= 1e3 ? (n/1e3).toFixed(1)+'K' : n.toString();

function initDates() {
  const now = new Date();
  document.getElementById('endDate').value = now.toISOString().slice(0,10);
  const start = new Date(now); start.setDate(start.getDate() - 30);
  document.getElementById('startDate').value = start.toISOString().slice(0,10);
}

async function loadModels() {
  try {
    const res = await fetch('/usage/models');
    const models = await res.json();
    const sel = document.getElementById('modelFilter');
    models.forEach(m => { const o = document.createElement('option'); o.value = m; o.textContent = m; sel.appendChild(o); });
  } catch(e) {}
}

async function loadData() {
  const g = document.getElementById('granularity').value;
  const s = document.getElementById('startDate').value;
  const e = document.getElementById('endDate').value;
  const m = document.getElementById('modelFilter').value;
  const params = new URLSearchParams({granularity: g});
  if (s) params.set('start', s);
  if (e) params.set('end', e + 'T23:59:59');
  if (m) params.set('model', m);

  const res = await fetch('/usage/stats?' + params);
  const json = await res.json();
  updateSummary(json.summary);
  updateTable(json.data);
  updateCharts(json.data, g);
}

function updateSummary(s) {
  document.getElementById('totalRequests').textContent = fmt(s.requestCount);
  document.getElementById('totalInput').textContent = fmt(s.inputTokens);
  document.getElementById('totalOutput').textContent = fmt(s.outputTokens);
  document.getElementById('totalTokens').textContent = fmt(s.totalTokens);
  const avg = s.requestCount > 0 ? Math.round(s.totalTokens / s.requestCount) : 0;
  document.getElementById('avgInput').textContent = s.requestCount > 0 ? '均 ' + fmt(Math.round(s.inputTokens/s.requestCount)) + '/次' : '';
  document.getElementById('avgOutput').textContent = s.requestCount > 0 ? '均 ' + fmt(Math.round(s.outputTokens/s.requestCount)) + '/次' : '';
  document.getElementById('avgTotal').textContent = s.requestCount > 0 ? '均 ' + fmt(avg) + '/次' : '';
}

function updateTable(data) {
  const tb = document.getElementById('tableBody');
  if (!data.length) { tb.innerHTML = '<tr><td colspan="6" class="empty">暂无数据</td></tr>'; return; }
  tb.innerHTML = data.map(r => `<tr><td>${r.period}</td><td>${r.model}</td><td>${r.requestCount}</td><td>${fmt(r.inputTokens)}</td><td>${fmt(r.outputTokens)}</td><td>${fmt(r.totalTokens)}</td></tr>`).join('');
}

function updateCharts(data, granularity) {
  const periods = [...new Set(data.map(r => r.period))].sort();
  const models = [...new Set(data.map(r => r.model))];
  const colors = ['#6366f1','#f59e0b','#10b981','#ef4444','#8b5cf6','#ec4899','#14b8a6','#f97316'];

  // Trend chart
  if (trendChart) trendChart.destroy();
  const trendDs = models.map((m, i) => ({
    label: m,
    data: periods.map(p => { const r = data.find(d => d.period === p && d.model === m); return r ? r.totalTokens : 0; }),
    borderColor: colors[i % colors.length],
    backgroundColor: colors[i % colors.length] + '20',
    fill: true, tension: 0.3
  }));
  trendChart = new Chart(document.getElementById('trendChart'), {
    type: 'line',
    data: { labels: periods, datasets: trendDs },
    options: { responsive: true, plugins: { legend: { position: 'top' } }, scales: { y: { beginAtZero: true, ticks: { callback: v => fmt(v) } } } }
  });

  // Model pie chart
  if (modelChart) modelChart.destroy();
  const modelTotals = models.map(m => data.filter(d => d.model === m).reduce((s, d) => s + d.totalTokens, 0));
  modelChart = new Chart(document.getElementById('modelChart'), {
    type: 'doughnut',
    data: { labels: models, datasets: [{ data: modelTotals, backgroundColor: colors.slice(0, models.length) }] },
    options: { responsive: true, plugins: { legend: { position: 'right' } } }
  });
}

document.getElementById('granularity').addEventListener('change', function() {
  const now = new Date(), s = document.getElementById('startDate');
  const offsets = { hour: 1, day: 30, week: 90, month: 365 };
  const d = new Date(now); d.setDate(d.getDate() - (offsets[this.value] || 30));
  s.value = d.toISOString().slice(0,10);
  loadData();
});

initDates(); loadModels(); loadData();
</script>
</body>
</html>
""";

record ClaudeMessagesRequest(string Model, bool Stream);

record KiroPingRequest(string? Prompt);

record ClaudeProxyRequest(
    string Model,
    ClaudeInputMessage[] Messages,
    bool Stream = false,
    object? System = null,
    int? MaxTokens = null);

record ClaudeInputMessage(string Role, object Content);

record ClaudeProxyResponse(
    string Id,
    string Type,
    string Role,
    string Model,
    ClaudeOutputContent[] Content,
    string? StopReason,
    string? StopSequence,
    ClaudeUsage Usage);

record ClaudeOutputContent(string Type, string? Text);

record ClaudeUsage(int InputTokens, int OutputTokens);

record ModelsResponse(string Object, ModelInfo[] Data, string DefaultModel, string Backend, string Source);

record ModelInfo(
    string Id,
    string Object,
    string OwnedBy,
    long Created,
    string Backend,
    bool IsDefault,
    string? Name,
    string? Description,
    string? RateUnit,
    int? MaxInputTokens,
    string Source);

record ProxyResult(int StatusCode, string Body)
{
    public static ProxyResult Json(int statusCode, object payload) =>
        new(statusCode, JsonSerializer.Serialize(payload));
}

record TokenRefreshResult(bool Success, string? ExpiresAt, string? Error);
