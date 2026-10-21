# csv-mcp

[English](./README.md) | [中文](./README.zh.md)

一个基于 **MCP（Model Context Protocol）** 的服务器，让 AI 代理可以通过普通的 **HTTP 传输**创建、
读取、追加、列出、下载和删除 CSV 文件。它解决了一个常见的痛点：代理手上有表格数据（例如查询结果），
却没有写文件或下载文件的工具，无法交给用户一个真正的 `.csv` 文件。有了 csv-mcp，代理只需要调用一次
`create_csv`，用户就能拿到可直接下载的 URL。

基于 **.NET 10**、官方 **MCP SDK 2.2.0** 和 **Streamable HTTP** 传输（POST JSON-RPC、SSE 帧响应——
与 Claude Desktop 及大多数 MCP 客户端默认使用的传输相同）。

---

## 目录

- [为什么需要 csv-mcp](#为什么需要-csv-mcp)
- [特性](#特性)
- [架构](#架构)
- [项目结构](#项目结构)
- [快速开始](#快速开始)
- [配置](#配置)
- [MCP 工具](#mcp-工具)
  - [代理的典型工作流](#代理的典型工作流)
  - [create_csv](#create_csv)
  - [append_csv_rows](#append_csv_rows)
  - [list_csv_files](#list_csv_files)
  - [get_csv_content](#get_csv_content)
  - [get_csv_download](#get_csv_download)
  - [delete_csv_file](#delete_csv_file)
- [原始下载端点](#原始下载端点)
- [结果封装与错误处理](#结果封装与错误处理)
- [设计原则与模式](#设计原则与模式)
- [扩展服务器](#扩展服务器)
- [测试](#测试)
- [安全注意事项](#安全注意事项)

---

## 为什么需要 csv-mcp

MCP 服务器通过 *工具*（tool）暴露能力——即命名、JSON 进 / JSON 出的可调用能力。一个典型缺口是：
代理可以*查询*数据（SQL、日志检索、API 调用），却无法持久化或交付数据。csv-mcp 用一组小而全的工具
填补了这个缺口：

| 能力 | 工具 |
|---|---|
| 用表头 + 行数据创建 CSV | `create_csv` |
| 向已有 CSV 追加行 | `append_csv_rows` |
| 列出已存储的文件 | `list_csv_files` |
| 回读文件的一段内容 | `get_csv_content` |
| 获取下载 URL（可选内联 Base64） | `get_csv_download` |
| 删除文件 | `delete_csv_file` |

所有校验都在服务端完成（绝不轻信代理的输入），所有写入都是原子的，并且在 `initialize` 阶段下发的
全局 **Instructions** 指南会精确告诉代理每个工具的用法——它由注册工具时使用的同一个目录生成，
单一事实来源。

## 特性

- **HTTP 传输**（Streamable HTTP，无状态）：MCP 客户端只需一个 URL（`http://localhost:5080/mcp`），
  无需管理 stdio 进程。
- **对代理友好**：每个工具都返回带 `downloadUrl` 的 JSON 封装，代理可以直接把它写进回复；
  浏览器可随时直接下载（`GET /download/{fileId}`）。
- **真正的 CSV 语义**：RFC 4180 转义、CRLF 换行、UTF-8 无 BOM、可配置分隔符（逗号 / 分号 / 制表符 / 竖线）。
- **安全设计**：触碰磁盘前先强制限制大小 / 行数 / 列数；阻止路径穿越；文件名净化；冲突时自动加后缀
  （`-1`、`-2`、……），绝不覆盖已有文件。
- **健壮的存储**：每个文件通过临时文件 + 原子重命名提交；JSON 边车文件（`{fileId}.meta.json`）记录
  分隔符、行列数、时间戳和文件名，即使重启后追加也能保持转义一致。
- **并发感知**：按文件粒度的 `SemaphoreSlim` 锁串行化对同一文件的追加；并行渲染加快大批量导出；
  后台服务定期清扫过期临时文件。
- **自我描述**：服务器为每个工具公布 `description`，并通过 MCP `instructions` 下发完整使用指南。

## 架构

```
MCP 客户端（代理）                     浏览器 / 下载管理器
      │  JSON-RPC (POST /mcp)                │   GET /download/{fileId}
      ▼                                      ▼
┌─────────────────────── Streamable HTTP ───────────────────────┐
│  csv-mcp（ASP.NET Core，.NET 10）                              │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ MCP 层（ModelContextProtocol.AspNetCore）                │  │
│  │  • tools/list、tools/call → ToolCollection               │  │
│  │  • initialize → serverInfo + 全局 instructions           │  │
│  └──────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ 工具层（薄编排，构造函数 DI）                             │  │
│  │  create_csv  append_csv_rows  list_csv_files             │  │
│  │  get_csv_content  get_csv_download  delete_csv_file      │  │
│  └──────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ 领域 + 服务                                              │  │
│  │  CsvFormatter         (RFC 4180 + 并行渲染)               │  │
│  │  CsvFileRepository    (原子 IO、边车文件、按文件锁、       │  │
│  │                         流式读取)                         │  │
│  │  CsvFileNamePolicy    (净化 + 唯一命名)                   │  │
│  │  TempFileCleanupService (后台维护)                        │  │
│  └──────────────────────────────────────────────────────────┘  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ 存储                                                     │  │
│  │  data/csv/{fileId}.csv        已提交的文件                │  │
│  │  data/csv/{fileId}.meta.json  边车元数据                  │  │
│  │  data/tmp/                    暂存与临时文件               │  │
│  └──────────────────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────────────────┘
```

## 项目结构

```
csv-mcp/
├── Directory.Build.props            # 共享 C# 设置（可空、警告即错误、文档）
├── CsvMcp.slnx                      # 解决方案文件
├── .gitignore                       # bin/obj、data/、临时文件、IDE 文件
├── README.md / README.zh.md         # 本文档
├── scripts/
│   └── smoke-test.ps1               # 面向正在运行的服务器做 11 步端到端测试
└── src/
    └── CsvMcp.Server/
        ├── CsvMcp.Server.csproj
        ├── appsettings.json         # 所有可调参数（见“配置”）
        ├── Program.cs               # 组合根：DI、MCP 注册、端点
        ├── Configuration/CsvStorageOptions.cs
        ├── Domain/                  # CsvMetadata、CsvReadResult、异常、分隔符
        ├── Services/
        │   ├── Csv/                 # 格式化器、文件命名策略
        │   ├── Storage/             # 仓储 + 边车模型 + 路径解析
        │   └── Maintenance/         # 临时文件清理后台服务
        ├── Instructions/            # ToolCatalog + GlobalUsageGuide（作为 MCP instructions 下发）
        ├── Tools/                   # 每个 MCP 工具一个文件
        └── Web/                     # 下载端点 + URL 构建器
```

## 快速开始

前置要求：.NET 10 SDK。

```bash
dotnet build src/CsvMcp.Server -c Release
dotnet run --project src/CsvMcp.Server -c Release --no-build
```

服务器监听在 `http://localhost:5080`（可通过 `Urls` 设置覆盖）。

验证：

```bash
# 根端点
Invoke-RestMethod http://localhost:5080/          # 或 curl http://localhost:5080/

# 端到端检查
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

### 连接 MCP 客户端

把 MCP 客户端指向 Streamable HTTP 端点：

- URL：`http://localhost:5080/mcp`
- 类型：`streamable-http`（HTTP + SSE）
- 无需认证（生产环境请自行增加中间件 / 反向代理）

客户端示例配置（Claude 风格）：

```json
{
  "mcpServers": {
    "csv-mcp": {
      "type": "http",
      "url": "http://localhost:5080/mcp"
    }
  }
}
```

## 配置

所有参数位于 `appsettings.json` 的 `CsvMcp` 段：

| 键 | 默认值 | 含义 |
|---|---|---|
| `Urls`（顶层） | `http://localhost:5080` | HTTP 监听地址 |
| `RootDirectory` | `data` | 基础目录（相对内容根目录解析） |
| `CsvSubdirectory` | `csv` | 已提交文件与边车文件所在目录 |
| `TempSubdirectory` | `tmp` | 暂存 / 临时文件所在目录 |
| `PublicBaseUrl` | `""` | 可选的下载链接固定基地址（为空时使用请求的 scheme/host） |
| `MaxRowsPerFile` | `1000000` | 单个文件数据行数上限 |
| `MaxColumns` | `10000` | 单个文件列数上限 |
| `MaxCellLength` | `1000000` | 单个单元格字符数上限 |
| `MaxFileSizeBytes` | `52428800`（50 MB） | 已提交文件大小上限 |
| `MaxAppendBytes` | `1000000`（约 1 MB） | 单次 `append_csv_rows` 调用可追加的渲染后字节数上限 |
| `DefaultContentRowLimit` | `200` | `get_csv_content` 默认分页大小 |
| `MaxContentRowLimit` | `100000` | `get_csv_content` 分页大小硬上限 |
| `MaxBase64DownloadBytes` | `5242880`（5 MB） | `includeBase64` 下载的大小上限 |
| `TempFileMaxAge` | `01:00:00` | 超过该时长的临时文件会被删除 |
| `CleanupInterval` | `00:10:00` | 后台清理任务的运行间隔（启动时也会清理一次） |

## MCP 工具

完整且始终最新的使用指南会在 `initialize` 阶段作为 MCP `instructions` 下发给客户端——代理无需外部文档
即可正确使用每个工具。

每个工具都返回相同的 JSON 封装，工具结果以 **字符串化 JSON** 的形式放在 MCP `text` 内容块中：

### 代理的典型工作流

1. 代理收集表格数据（来自它自己的查询 / 工具结果）。
2. 它调用 `create_csv`，传入表头与行数据，例如：

   ```json
   {
     "jsonrpc": "2.0",
     "id": 1,
     "method": "tools/call",
     "params": {
       "name": "create_csv",
       "arguments": {
         "headers": ["domain_id", "year_month", "user_id", "spins_in_month", "spins_remaining"],
         "rows": [
           ["1978", "2025-11", "3380506", "0", "8"],
           ["1978", "2025-11", "3389568", "0", "2"],
           ["1978", "2025-11", "4192031", "0", "25"],
           ["1978", "2025-11", "5151275", "0", "62"]
         ]
       }
     }
   }
   ```

3. 它收到 `fileId`、元数据和 `downloadUrl`，只需告诉用户：*“你的 CSV：http://localhost:5080/download/…”*
   ——不需要任何文件工具。
4. 如果数据分片到达，代理可以使用 `append_csv_rows`；用 `get_csv_content` 校验，用 `list_csv_files`
   列出，用 `delete_csv_file` 清理。

### create_csv

从表头 + 行数据创建新的 CSV 文件，返回元数据与下载 URL。若 `fileName` 已被占用，会自动加 `-1`、`-2`、……
后缀，并返回真实文件名，绝不覆盖已有文件。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `headers` | `string[]` | 是 | 列表头，例如 `["domain_id", "year_month"]` |
| `rows` | `string[][]` | 是 | 每行一个字符串数组，例如 `[["1978", "2025-11"]]` |
| `fileName` | `string?` | 否 | 建议文件名；非法字符会被剔除，保证 `.csv` 后缀；默认 `export-yyyyMMdd-HHmmss.csv` |
| `delimiter` | `string` | 否 | `comma`（默认）、`semicolon`、`tab` 或 `pipe` |

请求示例：

```json
{
  "jsonrpc": "2.0", "id": 1, "method": "tools/call",
  "params": { "name": "create_csv", "arguments": { "fileName": "member-spins.csv", "headers": ["a", "b"], "rows": [["1", "2"]] } }
}
```

响应示例（content 文本，此处美化排版）：

```json
{
  "ok": true,
  "tool": "create_csv",
  "data": {
    "fileId": "e958b77d7e3f49d4af9440ac7662907a",
    "fileName": "member-spins.csv",
    "rowCount": 1,
    "columnCount": 2,
    "sizeBytes": 9,
    "createdAtUtc": "2026-09-03T02:51:11.7762499+00:00",
    "updatedAtUtc": "2026-09-03T02:51:11.7762499+00:00",
    "downloadUrl": "http://localhost:5080/download/e958b77d7e3f49d4af9440ac7662907a"
  }
}
```

### append_csv_rows

向已有文件追加行。每行的单元格数必须与文件列数完全一致。对同一文件的追加按文件串行执行
（并发调用也安全）。内部实现为**并行渲染**（跨工作线程）+ **单次批量写入**，大流量追加也很快。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `fileId` | `string` | 是 | 已有文件的 ID |
| `rows` | `string[][]` | 是 | 要追加的行，结构与 `create_csv` 相同 |

单次调用最多可追加 `MaxAppendBytes` 的渲染后内容（默认 1,000,000 字节，约 1 MB）。数据更大时请拆成多次
`append_csv_rows` 调用——超限时错误信息会给出实际载荷大小。

### list_csv_files

列出已提交的文件，最新的在前。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `namePattern` | `string?` | 否 | 对文件名的大小写不敏感子串过滤 |
| `offset` | `int?` | 否 | 0 起始分页偏移，默认 0 |
| `limit` | `int?` | 否 | 最大条数（上限 1000）；默认全部 |

### get_csv_content

以纯文本 CSV 形式读取文件的一段内容，便于代理或用户核验数据。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `fileId` | `string` | 是 | 已有文件的 ID |
| `rowOffset` | `int` | 否 | 第一个数据行的 0 起始索引，默认 0 |
| `maxRows` | `int?` | 否 | 分页大小（收窄到 `[1, MaxContentRowLimit]`），默认 200 |
| `includeHeader` | `bool` | 否 | 是否在内容前附带表头行，默认 false |

响应包含 `rowsReturned`、`hasMore`、`totalDataRows` 和 `content` 文本。

### get_csv_download

返回下载 URL（可选：对小文件内联 Base64 载荷与 data URI）。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `fileId` | `string` | 是 | 已有文件的 ID |
| `includeBase64` | `bool` | 否 | 将文件以 Base64 + `data:text/csv;base64,…` URI 内嵌；超过 `MaxBase64DownloadBytes` 会被拒绝 |

### delete_csv_file

删除文件及其边车文件。幂等：删除不存在的 ID 返回 `ok: false`（“not found”），而不是报错。

## 原始下载端点

在 MCP 协议之外，供浏览器和下载管理器使用：

```
GET /download/{fileId}            → 附件下载（Content-Disposition: attachment; filename=…）
GET /download/{fileId}?inline=true → 内联预览
```

- 文件不存在时返回 `404` 及 JSON 正文
- `text/csv`，文件名按 RFC 5987 编码，支持 Range 请求（断点续传）

## 结果封装与错误处理

每个工具的结果文本都是以下两种之一：

```json
{ "ok": true,  "tool": "<工具名>", "data": { … } }
{ "ok": false, "tool": "<工具名>", "error": "人类可读、可安全展示的消息。" }
```

约定：

- 校验问题（形状错误、文件不存在、超出限制）作为 `ok: false` 的工具结果返回——代理可以读取 `error` 自行纠正。
- 消息绝不泄露路径或堆栈。
- 封装位于 MCP `text` 内容顶层，客户端可直接渲染或解析。

## 设计原则与模式

| 原则 / 模式 | 体现在哪里 |
|---|---|
| **单一事实来源** | 一个 `ToolCatalog` 同时驱动工具注册与生成的全局使用指南；工具描述就是同一方法上的 `[Description]` 特性 |
| **依赖倒置** | 工具依赖接口（`ICsvFileRepository`、`ICsvFormatter`、`ICsvStoragePaths`）；实现可替换、可 mock |
| **组合根** | 所有装配（选项、服务、工具、托管服务）都在 `Program.cs` 通过 `IServiceCollection` 完成；工具类就是由 DI 构造的普通 POCO |
| **全异步** | 所有 I/O 均为 `async`/`await`，贯穿 `CancellationToken`；请求路径上无阻塞调用 |
| **并发控制** | 按文件粒度的 `SemaphoreSlim` 门串行化对同一文件的追加，读取不受影响；门注册表用 `ConcurrentDictionary` |
| **并行提升吞吐** | `CsvFormatter` 用 `Parallel.For` 分块渲染行，然后顺序写入（输出有序、确定） |
| **原子提交** | 在 `data/tmp` 写临时文件、`File.Move` 就位、原子更新边车——崩溃也不会把半写文件当作已提交版本 |
| **边车元数据** | 分隔符（及行列数）被持久化，重启后追加仍能保持一致转义；列表优先走边车快路径，孤儿文件则重建元数据 |
| **后台维护** | `TempFileCleanupService`（托管 `BackgroundService`）在启动时和按间隔清扫过期临时文件 |
| **fail-safe 校验** | 所有限制在触碰磁盘前强制；显式 `CsvValidationException` 携带安全消息；构建开启警告即错误 |
| **路径安全** | 集中式路径解析，`fileId` 格式校验 + 穿越防护 |

## 扩展服务器

新增能力（例如 `update_csv` 或 `replace_csv`）只需四步：

1. **服务**——在存储契约（`ICsvFileRepository`）中加入该操作并实现（遵循按文件锁 + 原子写入模式）。
2. **工具**——在 `src/CsvMcp.Server/Tools/` 新建类，例如 `UpdateCsvTool`，带
   `[McpServerTool(Name = "update_csv", Title = "…")]` 方法，方法及每个参数都加 `[Description]`，
   校验失败抛出 / 返回 `CsvValidationException` 消息。
3. **接线**——在 `Program.cs` 的 DI 中注册新服务，并把工具类型加入 `toolTypes` 集合
   （保持静态类型为 `IEnumerable<Type>`——参见 `Program.cs` 中关于 `WithTools` 重载解析的注释）。
4. **文档**——在 `GlobalUsageGuide` 的 `ToolExamples` 中加入条目，让生成的 instructions 展示实例；
   并在 `README.md` 中提到该工具。

重新构建、重启后，该工具会自动出现在 `tools/list` 与 instructions 中。

## 测试

`scripts/smoke-test.ps1` 通过真实的 MCP 协议对运行中的服务器做端到端测试：

1. `initialize`（检查服务器名 + instructions 覆盖）
2. `tools/list`（检查全部 6 个工具 + 描述）
3. 用示例数据集 `create_csv`
4. 冲突时的唯一命名
5. 原始下载（字节精确比对、响应头）
6. 分页 `get_csv_content`
7. `list_csv_files`
8. `append_csv_rows`
9. 带 Base64 的 `get_csv_download`（解码并校验内容）
10. 校验与 404 行为
11. `delete_csv_file`（删除后再查 404）

运行方式（PowerShell 5.1 或 7）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
```

预期输出 `ALL CHECKS PASSED`，退出码 0。

## 安全注意事项

- 服务器默认只监听 `localhost`；真实部署请放在反向代理之后并增加认证（MCP 传输本身不带认证）。
- 文件名被净化和校验；`fileId` 限制为 `[0-9a-fA-F-]{1,64}`，额外的穿越尝试会被拒绝。
- 上传大小均有限制（行数、列数、单元格长度、文件总大小、Base64 载荷）。
- 删除需要知道精确的 `fileId`；ID 是 128 位随机值，实际上不可枚举。
- 全局 instructions 绝不暴露文件系统路径或服务器内部信息。
