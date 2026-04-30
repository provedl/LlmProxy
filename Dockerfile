FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 3000

# 安装 Node.js 20.x（Kiro API helper 需要）
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl ca-certificates && \
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && \
    apt-get install -y --no-install-recommends nodejs && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["LlmProxy.csproj", "."]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# 复制 Kiro API helper 脚本和 node 依赖
COPY kiro-api-helper.cjs .
COPY package.json package-lock.json ./
RUN npm ci --omit=dev

RUN mkdir -p /app/llm-cache /app/logs /app/data

VOLUME ["/app/data", "/app/llm-cache", "/app/logs"]

ENTRYPOINT ["dotnet", "LlmProxy.dll"]
