# LLM Proxy — 让 Claude CLI 使用 Kiro 订阅

将 Kiro 的 LLM 能力通过标准 Claude API 格式暴露出来，支持 Claude CLI、Hermes 等任何兼容 Anthropic Messages API 的客户端。

## 镜像信息

| 项目 | 值 |
|------|------|
| 镜像名 | `llm-proxy:latest` |
| 构建日期 | 2025-04-24 |
| 镜像大小 | ~155 MB |
| 基础镜像 | `mcr.microsoft.com/dotnet/aspnet:8.0` + Node.js 20.x |
| 导出文件 | `llm-proxy.tar` |

## 前提条件

- Docker Desktop 已安装
- Kiro IDE 已安装并登录过（本地有 SSO token 缓存）

## 快速开始

### 1. 导入镜像

```bash
docker load -i llm-proxy.tar
```

### 2. 找到你的 Kiro token 目录

Kiro 登录后会在本地缓存 SSO token，路径因系统而异：

| 系统 | 路径 |
|------|------|
| Windows | `C:\Users\<你的用户名>\.aws\sso\cache` |
| macOS | `/Users/<你的用户名>/.aws/sso/cache` |
| Linux | `/home/<你的用户名>/.aws/sso/cache` |

确认目录下有 `kiro-auth-token.json` 文件。如果没有，打开 Kiro IDE 登录一次即可自动生成。

### 3. 启动容器

**Windows (PowerShell):**
```powershell
docker run -d --name llm-proxy `
  -p 3000:3000 `
  -v "C:\Users\<你的用户名>\.aws\sso\cache:/app/tokens" `
  -e "Proxy__KiroApi__TokenFilePath=/app/tokens/kiro-auth-token.json" `
  -e "Proxy__KiroApi__RegistrationCacheDirectory=/app/tokens" `
  llm-proxy:latest
```

**macOS / Linux:**
```bash
docker run -d --name llm-proxy \
  -p 3000:3000 \
  -v ~/.aws/sso/cache:/app/tokens \
  -e Proxy__KiroApi__TokenFilePath=/app/tokens/kiro-auth-token.json \
  -e Proxy__KiroApi__RegistrationCacheDirectory=/app/tokens \
  llm-proxy:latest
```

### 4. 验证服务

```bash
curl http://localhost:3000/health
```

应返回 `{"status":"healthy", ...}`。

### 5. 配置 Claude CLI

```bash
# 设置环境变量
export ANTHROPIC_BASE_URL=http://localhost:3000
export ANTHROPIC_API_KEY=dummy-key

# 启动 Claude CLI
claude
```

Windows PowerShell:
```powershell
$env:ANTHROPIC_BASE_URL="http://localhost:3000"
$env:ANTHROPIC_API_KEY="dummy-key"
claude
```

> `ANTHROPIC_API_KEY` 随便填一个值即可，实际认证走 Kiro token。

### 6. 配置 Codex CLI（OpenAI）

LlmProxy 同时兼容 OpenAI Responses API，可以直接让 Codex CLI 使用。

编辑 `~/.codex/config.toml`：

```toml
model = "claude-sonnet-4"
model_provider = "kiro-proxy"

[model_providers.kiro-proxy]
name = "Kiro LLM Proxy"
base_url = "http://localhost:3000/v1"
env_key = "CODEX_PROXY_KEY"
wire_api = "responses"
```

然后设置环境变量（值随便填）：

```bash
export CODEX_PROXY_KEY=dummy-key
```

Windows PowerShell:
```powershell
$env:CODEX_PROXY_KEY="dummy-key"
```

启动 Codex：
```bash
codex
```

> 支持文本生成和 function calling（工具调用），Codex 的所有代码操作功能均可正常工作。

### 7. 持久化配置（可选）

不想每次都设环境变量，可以写入 shell 配置：

**bash/zsh (~/.bashrc 或 ~/.zshrc):**
```bash
export ANTHROPIC_BASE_URL=http://localhost:3000
export ANTHROPIC_API_KEY=dummy-key
```

**Windows 系统环境变量:**
```powershell
[System.Environment]::SetEnvironmentVariable("ANTHROPIC_BASE_URL", "http://localhost:3000", "User")
[System.Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", "dummy-key", "User")
```


## 常用命令

```bash
# 查看日志
docker logs llm-proxy

# 停止
docker stop llm-proxy

# 启动（已创建过的容器）
docker start llm-proxy

# 删除容器（重建时用）
docker rm -f llm-proxy

# LLM 连通性测试
curl http://localhost:3000/llm/test

# 查看缓存状态
curl http://localhost:3000/cache/stats

# 清空缓存
curl -X POST http://localhost:3000/cache/clear

# 查看可用模型
curl http://localhost:3000/v1/models

# Swagger 文档
# 浏览器打开 http://localhost:3000/swagger
```

## 工作原理

```
Claude CLI  ──stream:true──▶  LLM Proxy (:3000)  ──stream:false──▶  Kiro API
                                    │
                              转换为 SSE 流式输出
                              返回给 Claude CLI
```

- 接收 Claude CLI 的 streaming 请求
- 强制以非流式方式调用 Kiro API 获取完整响应
- 将完整响应转换为 Anthropic SSE streaming 格式返回
- 支持响应缓存（默认 24 小时，不缓存 tool_use 响应）

## Token 自动刷新

- Kiro token 有效期约 1 小时，proxy 会自动用 refreshToken 刷新
- 由于使用 volume 挂载，Kiro IDE 和 proxy 共享同一份 token 文件
- 只要 refreshToken 未过期（通常数天），无需手动干预
- 如果 refreshToken 过期，重新打开 Kiro IDE 登录即可

## 故障排查

| 问题 | 原因 | 解决 |
|------|------|------|
| `Connection refused` | 容器未启动 | `docker start llm-proxy` |
| `502 Bad Gateway` | Kiro token 过期 | 打开 Kiro IDE 重新登录 |
| `ENOENT: kiro-auth-token.json` | volume 挂载路径错误 | 检查 `-v` 参数路径是否正确 |
| `Node runtime not found` | 容器内 Node.js 异常 | 重建容器 `docker rm -f llm-proxy && docker run ...` |

## 端口冲突

如果 3000 端口被占用，可以映射到其他端口：

```bash
docker run -d --name llm-proxy -p 8888:3000 -v ... llm-proxy:latest
```

然后 Claude CLI 配置改为：
```bash
export ANTHROPIC_BASE_URL=http://localhost:8888
```
