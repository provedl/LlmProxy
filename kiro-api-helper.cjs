const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const https = require("https");
const { URL } = require("url");
const { CodeWhispererStreaming, GenerateAssistantResponseCommand } = require("@aws/codewhisperer-streaming-client");
const { SSOOIDCClient, CreateTokenCommand } = require("@aws-sdk/client-sso-oidc");

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) {
    chunks.push(chunk);
  }
  return Buffer.concat(chunks).toString("utf8").replace(/^\uFEFF/, "");
}

function parseJson(text) {
  return JSON.parse(text);
}

function writeJson(payload) {
  process.stdout.write(JSON.stringify(payload));
}

function makeError(statusCode, message, detail) {
  return {
    statusCode,
    body: JSON.stringify({
      error: message,
      ...(detail ? { detail } : {})
    })
  };
}

function readJsonFile(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function writeJsonFile(filePath, payload) {
  fs.writeFileSync(filePath, `${JSON.stringify(payload, null, 2)}\n`, "utf8");
}

function tokenExpiresSoon(token, offsetSeconds = 300) {
  if (!token?.expiresAt) {
    return true;
  }

  return new Date(token.expiresAt).getTime() <= Date.now() + offsetSeconds * 1000;
}

async function refreshTokenIfNeeded(tokenFilePath, registrationCacheDirectory) {
  const token = readJsonFile(tokenFilePath);
  if (!tokenExpiresSoon(token)) {
    return token;
  }

  if (!token.refreshToken || !token.clientIdHash) {
    throw new Error("Cached Kiro token cannot be refreshed");
  }

  const region = token.region || "us-east-1";
  const registrationPath = path.join(registrationCacheDirectory, `${token.clientIdHash}.json`);
  if (!fs.existsSync(registrationPath)) {
    throw new Error(`Client registration cache not found: ${registrationPath}`);
  }

  const registration = readJsonFile(registrationPath);
  const oidc = new SSOOIDCClient({ region });
  const response = await oidc.send(new CreateTokenCommand({
    clientId: registration.clientId,
    clientSecret: registration.clientSecret,
    grantType: "refresh_token",
    refreshToken: token.refreshToken
  }));

  const refreshed = {
    ...token,
    accessToken: response.accessToken,
    refreshToken: response.refreshToken || token.refreshToken,
    expiresAt: new Date(Date.now() + (response.expiresIn || 3600) * 1000).toISOString()
  };

  writeJsonFile(tokenFilePath, refreshed);
  return refreshed;
}

function extractTextParts(content) {
  if (typeof content === "string") {
    return [content];
  }

  if (!Array.isArray(content)) {
    return [];
  }

  return content
    .filter((part) => part && part.type === "text" && typeof part.text === "string")
    .map((part) => part.text);
}

function imageFormatFromMediaType(mediaType) {
  const normalized = String(mediaType || "").toLowerCase();
  if (normalized.includes("png")) return "png";
  if (normalized.includes("gif")) return "gif";
  if (normalized.includes("webp")) return "webp";
  return "jpeg";
}

function extractImages(content) {
  if (!Array.isArray(content)) {
    return [];
  }

  return content
    .filter((part) => part && part.type === "image" && part.source?.type === "base64" && typeof part.source.data === "string")
    .map((part) => ({
      format: imageFormatFromMediaType(part.source.media_type),
      source: {
        bytes: Buffer.from(part.source.data, "base64")
      }
    }));
}

function extractToolUses(content) {
  if (!Array.isArray(content)) {
    return [];
  }

  return content
    .filter((part) => part && part.type === "tool_use")
    .map((part, index) => ({
      toolUseId: part.id || `toolu_${index + 1}`,
      name: part.name,
      input: part.input || {}
    }))
    .filter((toolUse) => toolUse.name);
}

function toolResultContentToText(result) {
  if (typeof result === "string") {
    return result;
  }

  if (Array.isArray(result)) {
    return result
      .map((entry) => {
        if (typeof entry === "string") {
          return entry;
        }

        if (entry?.type === "text" && typeof entry.text === "string") {
          return entry.text;
        }

        return JSON.stringify(entry);
      })
      .join("\n\n");
  }

  if (result && typeof result === "object") {
    return JSON.stringify(result);
  }

  return "";
}

function extractToolResults(content) {
  if (!Array.isArray(content)) {
    return [];
  }

  return content
    .filter((part) => part && part.type === "tool_result")
    .map((part) => ({
      toolUseId: part.tool_use_id,
      content: [{ text: toolResultContentToText(part.content) }],
      status: part.is_error ? "error" : "success"
    }))
    .filter((toolResult) => toolResult.toolUseId);
}

function toolHistoryAsText(content) {
  if (!Array.isArray(content)) {
    return [];
  }

  return content
    .map((part) => {
      if (!part || typeof part !== "object") {
        return null;
      }

      if (part.type === "tool_use") {
        return [
          `[Tool use: ${part.name || "tool"}]`,
          part.id ? `id: ${part.id}` : null,
          `input: ${JSON.stringify(part.input || {})}`
        ].filter(Boolean).join("\n");
      }

      if (part.type === "tool_result") {
        return [
          `[Tool result${part.tool_use_id ? ` for ${part.tool_use_id}` : ""}${part.is_error ? " (error)" : ""}]`,
          toolResultContentToText(part.content)
        ].filter(Boolean).join("\n");
      }

      return null;
    })
    .filter(Boolean);
}

function convertClaudeMessageToKiroMessage(message, options = {}) {
  const textParts = extractTextParts(message.content);
  const text = [
    ...textParts,
    ...(options.preserveToolBlocks === false ? toolHistoryAsText(message.content) : [])
  ].join("\n\n");
  const images = extractImages(message.content);
  const toolUses = options.preserveToolBlocks === false ? [] : extractToolUses(message.content);
  const toolResults = options.preserveToolBlocks === false ? [] : extractToolResults(message.content);

  if (message.role === "assistant") {
    return {
      assistantResponseMessage: {
        content: text,
        ...(toolUses.length > 0 ? { toolUses } : {})
      }
    };
  }

  return {
    userInputMessage: {
      content: text,
      origin: "AI_EDITOR",
      ...(images.length > 0 ? { images } : {}),
      ...(toolResults.length > 0
        ? {
            userInputMessageContext: {
              toolResults
            }
          }
        : {})
    }
  };
}

function convertClaudeTools(tools) {
  if (!Array.isArray(tools) || tools.length === 0) {
    return undefined;
  }

  const converted = tools
    .map((tool) => {
      if (!tool?.name) {
        return null;
      }

      return {
        toolSpecification: {
          name: tool.name,
          description: tool.description || "",
          inputSchema: {
            json: tool.input_schema || {}
          }
        }
      };
    })
    .filter(Boolean);

  return converted.length > 0 ? converted : undefined;
}

function normalizeClaudeRequest(request) {
  const messages = Array.isArray(request.messages) ? [...request.messages] : [];
  const systemText = typeof request.system === "string"
    ? request.system
    : Array.isArray(request.system)
      ? request.system
          .filter((part) => part?.type === "text" && typeof part.text === "string")
          .map((part) => part.text)
          .join("\n\n")
      : "";

  if (systemText) {
    const firstUserIndex = messages.findIndex((message) => message.role === "user");
    if (firstUserIndex >= 0) {
      const first = messages[firstUserIndex];
      if (typeof first.content === "string") {
        first.content = `[System Instructions]\n${systemText}\n\n${first.content}`;
      } else if (Array.isArray(first.content)) {
        first.content = [
          { type: "text", text: `[System Instructions]\n${systemText}` },
          ...first.content
        ];
      }
    } else {
      messages.unshift({
        role: "user",
        content: `[System Instructions]\n${systemText}`
      });
    }
  }

  return {
    ...request,
    messages
  };
}

function buildConversationState(request) {
  const normalized = normalizeClaudeRequest(request);
  const tools = convertClaudeTools(normalized.tools);
  const kiroMessages = normalized.messages.map((message) =>
    convertClaudeMessageToKiroMessage(message, {
      preserveToolBlocks: Boolean(tools)
    })
  );

  if (kiroMessages.length === 0) {
    throw new Error("No messages provided");
  }

  const currentMessage = kiroMessages[kiroMessages.length - 1];
  if (!currentMessage.userInputMessage) {
    throw new Error("Latest message must be a user message");
  }

  if (tools) {
    currentMessage.userInputMessage.userInputMessageContext = {
      ...(currentMessage.userInputMessage.userInputMessageContext || {}),
      tools
    };
  }

  return {
    conversationId: crypto.randomUUID(),
    currentMessage,
    history: kiroMessages.slice(0, -1),
    chatTriggerType: "MANUAL"
  };
}

function mergeToolInput(existing, incoming) {
  if (incoming === undefined || incoming === null) {
    return existing;
  }

  if (typeof incoming === "string") {
    return `${typeof existing === "string" ? existing : ""}${incoming}`;
  }

  if (typeof existing === "string") {
    return incoming;
  }

  if (typeof incoming === "object") {
    return {
      ...(existing && typeof existing === "object" ? existing : {}),
      ...incoming
    };
  }

  return incoming;
}

function estimateTokens(text) {
  if (!text) return 0;
  let count = 0;
  for (const ch of text) {
    count += ch.charCodeAt(0) > 0x7f ? 0.6 : 0.25;
  }
  return Math.ceil(count);
}

function estimateInputTokens(request) {
  let text = "";
  if (request.system) {
    if (typeof request.system === "string") {
      text += request.system;
    } else if (Array.isArray(request.system)) {
      for (const block of request.system) {
        if (block.text) text += block.text;
      }
    }
  }
  if (Array.isArray(request.messages)) {
    text += JSON.stringify(request.messages);
  }
  if (Array.isArray(request.tools)) {
    text += JSON.stringify(request.tools);
  }
  return estimateTokens(text);
}

/**
 * Extract XML-formatted tool calls from text output.
 * Some models output tool calls in XML format instead of via toolUseEvent.
 * Supported formats:
 *   - Standard: [invoke name="Tool"][parameter name="p"]value[/parameter][/invoke]
 *   - Anthropic style: [antml:invoke name="Tool"][antml:parameter name="p"]value[/antml:parameter][/antml:invoke]
 *   - Function calls style: [antml:function_calls][antml:invoke]...[/antml:invoke][/antml:function_calls]
 * (Note: XML tags shown with [] to avoid parsing issues in source)
 * Returns { cleanedText, extractedTools }
 */
function extractXmlToolCalls(text) {
  if (!text || typeof text !== "string") {
    return { cleanedText: text || "", extractedTools: [] };
  }

  const extractedTools = [];
  let cleanedText = text;

  // Pattern 1: antml:function_calls wrapper with antml:invoke blocks
  const functionCallsPattern = /<function_calls>([\s\S]*?)<\/antml:function_calls>/gi;
  const invokePattern = /<invoke\s+name="([^"]+)">([\s\S]*?)<\/antml:invoke>/gi;
  const paramPattern = /<parameter\s+name="([^"]+)">([\s\S]*?)<\/antml:parameter>/gi;

  // Pattern 2: Simple invoke blocks (without antml: prefix)
  const simpleInvokePattern = /<invoke\s+name="([^"]+)">([\s\S]*?)<\/invoke>/gi;
  const simpleParamPattern = /<parameter\s+name="([^"]+)">([\s\S]*?)<\/parameter>/gi;

  // First, try to extract from antml:function_calls wrapper
  let functionCallsMatch;
  while ((functionCallsMatch = functionCallsPattern.exec(text)) !== null) {
    const innerContent = functionCallsMatch[1];
    let invokeMatch;
    const localInvokePattern = /<invoke\s+name="([^"]+)">([\s\S]*?)<\/antml:invoke>/gi;

    while ((invokeMatch = localInvokePattern.exec(innerContent)) !== null) {
      const toolName = invokeMatch[1];
      const paramsContent = invokeMatch[2];
      const params = {};

      let paramMatch;
      const localParamPattern = /<parameter\s+name="([^"]+)">([\s\S]*?)<\/antml:parameter>/gi;
      while ((paramMatch = localParamPattern.exec(paramsContent)) !== null) {
        const paramName = paramMatch[1];
        let paramValue = paramMatch[2];

        // Try to parse as JSON if it looks like JSON
        if (paramValue.trim().startsWith("{") || paramValue.trim().startsWith("[")) {
          try {
            paramValue = JSON.parse(paramValue);
          } catch {
            // Keep as string
          }
        }
        params[paramName] = paramValue;
      }

      extractedTools.push({
        id: `toolu_xml_${crypto.randomUUID().replace(/-/g, "").slice(0, 12)}`,
        name: toolName,
        input: params
      });
    }

    // Remove the matched function_calls block from text
    cleanedText = cleanedText.replace(functionCallsMatch[0], "");
  }

  // Also try standalone antml:invoke blocks (not wrapped in function_calls)
  let standaloneInvokeMatch;
  const standaloneInvokePattern = /<invoke\s+name="([^"]+)">([\s\S]*?)<\/antml:invoke>/gi;
  while ((standaloneInvokeMatch = standaloneInvokePattern.exec(cleanedText)) !== null) {
    const toolName = standaloneInvokeMatch[1];
    const paramsContent = standaloneInvokeMatch[2];
    const params = {};

    let paramMatch;
    const localParamPattern = /<parameter\s+name="([^"]+)">([\s\S]*?)<\/antml:parameter>/gi;
    while ((paramMatch = localParamPattern.exec(paramsContent)) !== null) {
      const paramName = paramMatch[1];
      let paramValue = paramMatch[2];

      if (paramValue.trim().startsWith("{") || paramValue.trim().startsWith("[")) {
        try {
          paramValue = JSON.parse(paramValue);
        } catch {
          // Keep as string
        }
      }
      params[paramName] = paramValue;
    }

    extractedTools.push({
      id: `toolu_xml_${crypto.randomUUID().replace(/-/g, "").slice(0, 12)}`,
      name: toolName,
      input: params
    });

    cleanedText = cleanedText.replace(standaloneInvokeMatch[0], "");
  }

  // Try simple invoke pattern (without antml: prefix)
  let simpleMatch;
  while ((simpleMatch = simpleInvokePattern.exec(cleanedText)) !== null) {
    const toolName = simpleMatch[1];
    const paramsContent = simpleMatch[2];
    const params = {};

    let paramMatch;
    const localParamPattern = /<parameter\s+name="([^"]+)">([\s\S]*?)<\/parameter>/gi;
    while ((paramMatch = localParamPattern.exec(paramsContent)) !== null) {
      const paramName = paramMatch[1];
      let paramValue = paramMatch[2];

      if (paramValue.trim().startsWith("{") || paramValue.trim().startsWith("[")) {
        try {
          paramValue = JSON.parse(paramValue);
        } catch {
          // Keep as string
        }
      }
      params[paramName] = paramValue;
    }

    extractedTools.push({
      id: `toolu_xml_${crypto.randomUUID().replace(/-/g, "").slice(0, 12)}`,
      name: toolName,
      input: params
    });

    cleanedText = cleanedText.replace(simpleMatch[0], "");
  }

  // Clean up any remaining whitespace from removals
  cleanedText = cleanedText.trim();

  return { cleanedText, extractedTools };
}

function buildClaudeResponse(request, fullText, toolUses) {
  // Extract any XML-formatted tool calls from the text
  const { cleanedText, extractedTools } = extractXmlToolCalls(fullText);

  // Merge extracted XML tools with toolUseEvent tools
  const allToolUses = [...toolUses, ...extractedTools];

  const content = [];
  if (cleanedText) {
    content.push({
      type: "text",
      text: cleanedText
    });
  }

  for (const toolUse of allToolUses) {
    let input = toolUse.input;
    if (typeof input === "string") {
      try {
        input = JSON.parse(input);
      } catch {
        input = { raw: input };
      }
    }

    content.push({
      type: "tool_use",
      id: toolUse.id,
      name: toolUse.name,
      input: input || {}
    });
  }

  let outputText = cleanedText || "";
  for (const tu of allToolUses) {
    outputText += JSON.stringify(tu.input || {});
  }

  return {
    id: `msg_${crypto.randomUUID().replace(/-/g, "")}`,
    type: "message",
    role: "assistant",
    model: request.model || "kiro",
    content,
    stop_reason: allToolUses.length > 0 ? "tool_use" : "end_turn",
    stop_sequence: null,
    usage: {
      input_tokens: estimateInputTokens(request),
      output_tokens: estimateTokens(outputText)
    }
  };
}

function addHeaders(client, contentCollectionEnabled, agentMode, authMethod) {
  if (!contentCollectionEnabled) {
    client.middlewareStack.add(
      (next) => async (args) => {
        args.request.headers = {
          ...args.request.headers,
          "x-amzn-codewhisperer-optout": "true"
        };
        return next(args);
      },
      { step: "build" }
    );
  }

  if (agentMode) {
    client.middlewareStack.add(
      (next) => async (args) => {
        args.request.headers = {
          ...args.request.headers,
          "x-amzn-kiro-agent-mode": agentMode
        };
        return next(args);
      },
      { step: "build" }
    );
  }

  if (authMethod === "external_idp") {
    client.middlewareStack.add(
      (next) => async (args) => {
        args.request.headers = {
          ...args.request.headers,
          TokenType: "EXTERNAL_IDP"
        };
        return next(args);
      },
      { step: "build" }
    );
  }
}

function buildRequestHeaders(token, options, extraHeaders = {}) {
  const headers = {
    Authorization: `Bearer ${token.accessToken}`,
    ...extraHeaders
  };

  if (!options.contentCollectionEnabled) {
    headers["x-amzn-codewhisperer-optout"] = "true";
  }

  if (options.agentMode) {
    headers["x-amzn-kiro-agent-mode"] = options.agentMode;
  }

  if (token.authMethod === "external_idp") {
    headers.TokenType = "EXTERNAL_IDP";
  }

  return headers;
}

function sendHttpRequest(method, endpoint, requestPath, headers, body) {
  return new Promise((resolve, reject) => {
    const url = new URL(endpoint);
    const request = https.request({
      protocol: url.protocol,
      hostname: url.hostname,
      port: url.port || undefined,
      method,
      path: requestPath,
      headers
    }, (response) => {
      const chunks = [];
      response.on("data", (chunk) => chunks.push(chunk));
      response.on("end", () => {
        const text = Buffer.concat(chunks).toString("utf8");
        resolve({
          statusCode: response.statusCode || 0,
          body: text
        });
      });
    });

    request.on("error", reject);

    if (body) {
      request.write(body);
    }

    request.end();
  });
}

function parseJsonResponse(response, actionName) {
  if (response.statusCode < 200 || response.statusCode >= 300) {
    throw new Error(`${actionName} failed with status ${response.statusCode}: ${response.body}`);
  }

  if (!response.body) {
    return {};
  }

  return JSON.parse(response.body);
}

async function listAvailableProfiles(token, options) {
  let nextToken;
  const profiles = [];

  do {
    const body = JSON.stringify({
      ...(nextToken ? { nextToken } : {})
    });
    const response = await sendHttpRequest(
      "POST",
      options.endpoint,
      "/ListAvailableProfiles",
      buildRequestHeaders(token, options, {
        "content-type": "application/json",
        "content-length": Buffer.byteLength(body)
      }),
      body
    );

    const parsed = parseJsonResponse(response, "ListAvailableProfiles");
    profiles.push(...(Array.isArray(parsed.profiles) ? parsed.profiles : []));
    nextToken = parsed.nextToken;
  } while (nextToken);

  return profiles;
}

async function resolveProfileArn(token, options) {
  if (token.profileArn) {
    return token.profileArn;
  }

  const profiles = await listAvailableProfiles(token, options);
  const firstProfile = profiles.find((profile) => typeof profile?.arn === "string" && profile.arn.trim());
  if (!firstProfile) {
    throw new Error("No profile ARN available for Kiro model discovery");
  }

  const updatedToken = {
    ...token,
    profileArn: firstProfile.arn
  };
  writeJsonFile(options.tokenFilePath, updatedToken);
  return firstProfile.arn;
}

async function listAvailableModels(options) {
  const token = await refreshTokenIfNeeded(options.tokenFilePath, options.registrationCacheDirectory);
  const profileArn = await resolveProfileArn(token, options);
  const models = [];
  let defaultModel;
  let nextToken;

  do {
    const query = new URLSearchParams({
      origin: "AI_EDITOR",
      profileArn
    });

    if (nextToken) {
      query.set("nextToken", nextToken);
    }

    const response = await sendHttpRequest(
      "GET",
      options.endpoint,
      `/ListAvailableModels?${query.toString()}`,
      buildRequestHeaders(token, options),
      null
    );

    const parsed = parseJsonResponse(response, "ListAvailableModels");
    models.push(...(Array.isArray(parsed.models) ? parsed.models : []));
    if (!defaultModel && parsed.defaultModel) {
      defaultModel = parsed.defaultModel;
    }
    nextToken = parsed.nextToken;
  } while (nextToken);

  const availableModels = models
    .filter((model) => typeof model?.modelId === "string" && model.modelId.trim())
    .map((model) => ({
      id: model.modelId,
      name: model.modelName || model.modelId,
      description: model.description || null,
      rateMultiplier: model.rateMultiplier ?? null,
      rateUnit: model.rateUnit || null,
      maxInputTokens: model.tokenLimits?.maxInputTokens ?? null
    }));

  return {
    models: availableModels,
    defaultModel: defaultModel?.modelId
      ? {
          id: defaultModel.modelId,
          name: defaultModel.modelName || defaultModel.modelId,
          description: defaultModel.description || null,
          rateMultiplier: defaultModel.rateMultiplier ?? null,
          rateUnit: defaultModel.rateUnit || null,
          maxInputTokens: defaultModel.tokenLimits?.maxInputTokens ?? null
        }
      : null
  };
}

async function callKiroApi(request, options) {
  const conversationState = buildConversationState(request);
  const token = await refreshTokenIfNeeded(options.tokenFilePath, options.registrationCacheDirectory);
  const profileArn = await resolveProfileArn(token, options);

  const client = new CodeWhispererStreaming({
    region: options.region,
    endpoint: options.endpoint,
    token: { token: token.accessToken }
  });

  addHeaders(
    client,
    options.contentCollectionEnabled,
    options.agentMode,
    token.authMethod
  );

  const command = new GenerateAssistantResponseCommand({
    conversationState,
    profileArn
  });

  let response;
  try {
    response = await client.send(command);
  } catch (error) {
    if (error?.name === "AccessDeniedException" || error?.name === "UnauthorizedException") {
      const refreshedToken = await refreshTokenIfNeeded(options.tokenFilePath, options.registrationCacheDirectory);
      const retryClient = new CodeWhispererStreaming({
        region: options.region,
        endpoint: options.endpoint,
        token: { token: refreshedToken.accessToken }
      });

      addHeaders(
        retryClient,
        options.contentCollectionEnabled,
        options.agentMode,
        refreshedToken.authMethod
      );

      response = await retryClient.send(command);
    } else {
      throw error;
    }
  }

  const responseStream = response.generateAssistantResponseResponse || response.sendMessageResponse;
  if (!responseStream) {
    throw new Error("No response stream returned from Kiro API");
  }

  let fullText = "";
  const toolUseMap = new Map();

  for await (const event of responseStream) {
    if (event.assistantResponseEvent?.content) {
      fullText += event.assistantResponseEvent.content;
    }

    if (event.toolUseEvent) {
      const id = event.toolUseEvent.toolUseId || `toolu_${toolUseMap.size + 1}`;
      const existing = toolUseMap.get(id) || {
        id,
        name: event.toolUseEvent.name || "tool",
        input: {}
      };

      if (event.toolUseEvent.name) {
        existing.name = event.toolUseEvent.name;
      }

      existing.input = mergeToolInput(existing.input, event.toolUseEvent.input);
      toolUseMap.set(id, existing);
    }
  }

  return buildClaudeResponse(request, fullText, [...toolUseMap.values()]);
}

async function main() {
  try {
    const rawInput = await readStdin();
    const payload = parseJson(rawInput);
    const action = payload.action || "chat";
    const request = payload.requestBody ? parseJson(payload.requestBody) : null;
    const options = {
      region: payload.options.region || "us-east-1",
      endpoint: payload.options.endpoint || "https://q.us-east-1.amazonaws.com",
      tokenFilePath: payload.options.tokenFilePath,
      registrationCacheDirectory: payload.options.registrationCacheDirectory,
      contentCollectionEnabled: !!payload.options.contentCollectionEnabled,
      agentMode: payload.options.agentMode || "vibe"
    };

    const response = action === "listModels"
      ? await listAvailableModels(options)
      : await callKiroApi(request, options);

    writeJson({
      statusCode: 200,
      body: JSON.stringify(response)
    });
  } catch (error) {
    const message = error?.message || "Unknown Kiro API error";
    let statusCode = 502;

    if (error?.name === "AccessDeniedException") {
      statusCode = 401;
    } else if (error?.name === "ThrottlingException") {
      statusCode = 429;
    } else if (error?.name === "ValidationException") {
      statusCode = 400;
    }

    writeJson(makeError(statusCode, "Kiro API request failed", message));
    process.exitCode = 1;
  }
}

main();
