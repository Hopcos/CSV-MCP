<#
.SYNOPSIS
    End-to-end smoke test for the csv-mcp MCP server.

.DESCRIPTION
    Drives the server over the MCP Streamable HTTP transport (JSON-RPC / SSE framing) and verifies
    the full CSV lifecycle: initialize, tools/list, create_csv -> uniqueness -> download -> content
    -> list -> append -> base64 download -> delete, plus error and 404 cases.

    Target: Windows PowerShell 5.1. Note that JSON-RPC arguments are passed as pre-built JSON strings
    because ConvertTo-Json unrolls single-element arrays, which would corrupt nested row arrays.

.PARAMETER BaseUrl
    Server base URL (default http://127.0.0.1:5080).

.PARAMETER McpPath
    MCP endpoint path (default /mcp).

.EXAMPLE
    scripts\smoke-test.ps1
#>
param(
    [string]$BaseUrl = "http://127.0.0.1:5080",
    [string]$McpPath = "/mcp"
)

$ErrorActionPreference = "Stop"

$mcpUrl = "$BaseUrl$McpPath"
$requestId = 0
$failures = 0

$headers = @{
    "Accept"               = "application/json, text/event-stream"
    "MCP-Protocol-Version" = "2025-11-25"
}

function Assert-True([bool]$condition, [string]$message) {
    if ($condition) {
        Write-Host "  PASS: $message" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL: $message" -ForegroundColor Red
        $script:failures++
    }
}

function Invoke-Mcp([string]$method, [string]$paramsJson) {
    $script:requestId++
    $body = '{"jsonrpc":"2.0","id":' + $script:requestId + ',"method":"' + $method + '","params":' + $paramsJson + '}'
    $raw = (Invoke-WebRequest -Uri $mcpUrl -Method Post -Headers $headers -Body $body -ContentType "application/json" -UseBasicParsing).Content

    # Streamable HTTP frames responses as SSE (event: message / data: ...); accept plain JSON too.
    $jsonText = $raw
    if ($raw -match "(?m)^event: message$") {
        if ($raw -notmatch "(?m)^data: (?<json>.*)$") {
            throw "Malformed SSE response for '$method': $raw"
        }
        $jsonText = $Matches["json"]
    }

    $response = $jsonText | ConvertFrom-Json
    if ($null -ne $response.result) {
        return $response.result
    }
    throw "JSON-RPC error for '$method': $($response.error | ConvertTo-Json -Depth 6)"
}

function Invoke-Tool([string]$toolName, [string]$argumentsJson) {
    $paramsJson = '{"name":"' + $toolName + '","arguments":' + $argumentsJson + '}'
    $result = Invoke-Mcp "tools/call" $paramsJson
    $text = $result.content[0].text
    return $text | ConvertFrom-Json
}

function Invoke-ToolWithParams([string]$toolName, $argumentsHashtable) {
    # Serialize nested rows separately so single-element arrays survive (PS 5.1 workaround).
    if ($argumentsHashtable.ContainsKey("rows")) {
        # Build the nested rows JSON with foreach (never the pipeline): PowerShell 5.1 pipelines
        # flatten nested single-element arrays, which would corrupt the row structure.
        $rowsJsonParts = @()
        foreach ($row in $argumentsHashtable["rows"]) {
            $cells = @()
            foreach ($cell in $row) {
                $cells += '"' + ($cell -replace '"', '\"') + '"'
            }
            $rowsJsonParts += '[' + ($cells -join ',') + ']'
        }
        $rowsJson = $rowsJsonParts -join ','
        $argumentsHashtable.Remove("rows")
        $json = $argumentsHashtable | ConvertTo-Json -Depth 8 -Compress
        # Re-insert rows as a hand-built nested array.
        $json = $json.Substring(0, $json.Length - 1) + ',"rows":[' + $rowsJson + ']}'
        return Invoke-Tool $toolName $json
    }
    return Invoke-Tool $toolName ($argumentsHashtable | ConvertTo-Json -Depth 8 -Compress)
}

Write-Host "== csv-mcp smoke test against $mcpUrl ==" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
Write-Host "[0] reset: delete any leftover files from previous runs"
# ---------------------------------------------------------------------------
$leftover = Invoke-Tool "list_csv_files" "{}"
foreach ($file in @($leftover.data.files)) {
    $null = Invoke-Tool "delete_csv_file" ('{"fileId":"' + $file.fileId + '"}')
}

# ---------------------------------------------------------------------------
Write-Host "[1/11] initialize"
# ---------------------------------------------------------------------------
$init = Invoke-Mcp "initialize" '{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"csv-mcp-smoke-test","version":"1.0.0"}}'

Assert-True ($init.serverInfo.name -eq "csv-mcp") "serverInfo.name is 'csv-mcp' (got '$($init.serverInfo.name)')"
Assert-True ($null -ne $init.instructions -and $init.instructions.Contains("create_csv")) "instructions mention create_csv"
foreach ($toolName in @("create_csv", "append_csv_rows", "list_csv_files", "get_csv_content", "get_csv_download", "delete_csv_file")) {
    Assert-True ($init.instructions.Contains($toolName)) "instructions cover tool '$toolName'"
}

# ---------------------------------------------------------------------------
Write-Host "[2/11] tools/list"
# ---------------------------------------------------------------------------
$toolList = (Invoke-Mcp "tools/list" "{}").tools
$expectedTools = @("create_csv", "append_csv_rows", "list_csv_files", "get_csv_content", "get_csv_download", "delete_csv_file")
Assert-True ($toolList.Count -eq $expectedTools.Count) "tools/list returns $($expectedTools.Count) tools (got $($toolList.Count))"
foreach ($expected in $expectedTools) {
    $tool = $toolList | Where-Object { $_.name -eq $expected }
    Assert-True ($null -ne $tool) "tool '$expected' is advertised"
    Assert-True (-not [string]::IsNullOrWhiteSpace($tool.description)) "tool '$expected' has a description"
}

# ---------------------------------------------------------------------------
Write-Host "[3/11] create_csv with the sample dataset"
# ---------------------------------------------------------------------------
$createResult = Invoke-ToolWithParams "create_csv" @{
    fileName = "member-spins.csv"
    headers  = @("domain_id", "year_month", "user_id", "spins_in_month", "spins_remaining")
    # NOTE: unary comma (,@(...)) on EACH row keeps it nested — @(...) would unroll the row
    # into its cells inside the outer @() array subexpression in PowerShell.
    rows     = @(
        ,@("1978", "2025-11", "3380506", "0", "8")
        ,@("1978", "2025-11", "3389568", "0", "2")
        ,@("1978", "2025-11", "4192031", "0", "25")
        ,@("1978", "2025-11", "5151275", "0", "62")
    )
    delimiter = "comma"
}
Assert-True ($createResult.ok -eq $true) "create_csv succeeds"
$fileId = $createResult.data.fileId
Assert-True ($fileId -match "^[0-9a-f]{32}$") "created fileId is a 32-char hex id (got '$fileId')"
Assert-True ($createResult.data.fileName -eq "member-spins.csv") "fileName is 'member-spins.csv'"
Assert-True ($createResult.data.rowCount -eq 4) "rowCount is 4"
Assert-True ($createResult.data.columnCount -eq 5) "columnCount is 5"
Assert-True ($null -ne $createResult.data.downloadUrl -and $createResult.data.downloadUrl.Contains("/download/$fileId")) "downloadUrl points at /download/{fileId}"

# ---------------------------------------------------------------------------
Write-Host "[4/11] unique file naming on collision"
# ---------------------------------------------------------------------------
$secondCreate = Invoke-ToolWithParams "create_csv" @{
    fileName = "member-spins.csv"
    headers  = @("a", "b")
    rows     = ,@("1", "2")
}
Assert-True ($secondCreate.ok -eq $true) "second create with the same name succeeds"
Assert-True ($secondCreate.data.fileName -eq "member-spins-1.csv") "collision resolved to 'member-spins-1.csv' (got '$($secondCreate.data.fileName)')"
$secondFileId = $secondCreate.data.fileId

# ---------------------------------------------------------------------------
Write-Host "[5/11] download endpoint serves the exact CSV"
# ---------------------------------------------------------------------------
$download = Invoke-WebRequest -Uri $createResult.data.downloadUrl -Method Get -UseBasicParsing
Assert-True ($download.StatusCode -eq 200) "GET downloadUrl returns HTTP 200"
Assert-True ($download.Headers["Content-Type"] -like "text/csv*") "download content type is text/csv"
Assert-True ($download.Headers["Content-Disposition"] -like "*attachment*") "download Content-Disposition is attachment"
$expectedCsv = "domain_id,year_month,user_id,spins_in_month,spins_remaining`r`n" +
               "1978,2025-11,3380506,0,8`r`n" +
               "1978,2025-11,3389568,0,2`r`n" +
               "1978,2025-11,4192031,0,25`r`n" +
               "1978,2025-11,5151275,0,62`r`n"
Assert-True (($download.Content -replace "`r`n", "`n") -eq ($expectedCsv -replace "`r`n", "`n")) "downloaded bytes match the sample dataset"

# ---------------------------------------------------------------------------
Write-Host "[6/11] get_csv_content (paged read-back)"
# ---------------------------------------------------------------------------
$contentResult = Invoke-Tool "get_csv_content" ('{"fileId":"' + $fileId + '","rowOffset":0,"maxRows":2,"includeHeader":true}')
Assert-True ($contentResult.ok -eq $true) "get_csv_content succeeds"
Assert-True ($contentResult.data.rowsReturned -eq 2) "returns 2 rows as requested"
Assert-True ($contentResult.data.hasMore -eq $true) "reports more rows available"
Assert-True ($contentResult.data.content.StartsWith("domain_id,")) "content starts with the header row"

# ---------------------------------------------------------------------------
Write-Host "[7/11] list_csv_files"
# ---------------------------------------------------------------------------
$listResult = Invoke-Tool "list_csv_files" '{"limit":10}'
Assert-True ($listResult.ok -eq $true) "list_csv_files succeeds"
Assert-True ($listResult.data.returned -ge 2) "at least two files listed"
Assert-True (@($listResult.data.files | Where-Object { $_.fileId -eq $fileId }).Count -eq 1) "created file appears in the listing"
Assert-True (@($listResult.data.files | Where-Object { $_.fileId -eq $secondFileId }).Count -eq 1) "second file appears in the listing"

# ---------------------------------------------------------------------------
Write-Host "[8/11] append_csv_rows"
# ---------------------------------------------------------------------------
$appendResult = Invoke-ToolWithParams "append_csv_rows" @{
    fileId = $fileId
    rows   = ,@("1978", "2025-11", "6012345", "1", "4")
}
Assert-True ($appendResult.ok -eq $true) "append_csv_rows succeeds"
Assert-True ($appendResult.data.rowCount -eq 5) "rowCount grows to 5 after append"

# Large (but legal) batched append: 5,000 rows x 5 cols exercises the parallel-render + single-write path.
$bigRows = @()
foreach ($i in 1..5000) {
    $bigRows += ,@("r$i", "x", "x", "x", "x")
}
$bigAppend = Invoke-ToolWithParams "append_csv_rows" @{ fileId = $fileId; rows = $bigRows }
Assert-True ($bigAppend.ok -eq $true) "append of 5,000 rows succeeds"
Assert-True ($bigAppend.data.rowCount -eq 5005) "rowCount grows to 5005 after batched append"

# Over-cap append: one row of 5 cells x 250,000 chars (~1.25 MB rendered) must be rejected by MaxAppendBytes.
$hugeRow = @()
foreach ($i in 1..5) { $hugeRow += ('x' * 250000) }
$overAppend = Invoke-ToolWithParams "append_csv_rows" @{ fileId = $fileId; rows = ,$hugeRow }
Assert-True ($overAppend.ok -eq $false) "append over MaxAppendBytes is rejected"
Assert-True ($overAppend.error.Contains("MaxAppendBytes")) "rejection message names MaxAppendBytes"

# ---------------------------------------------------------------------------
Write-Host "[9/11] get_csv_download with inline base64"
# ---------------------------------------------------------------------------
$downloadResult = Invoke-Tool "get_csv_download" ('{"fileId":"' + $fileId + '","includeBase64":true}')
Assert-True ($downloadResult.ok -eq $true) "get_csv_download succeeds"
Assert-True ($null -ne $downloadResult.data.base64) "base64 content is embedded"
Assert-True ($downloadResult.data.dataUri.StartsWith("data:text/csv;base64,")) "data URI is well-formed"
$decoded = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($downloadResult.data.base64))
Assert-True ($decoded.Contains("6012345")) "decoded base64 contains the appended row"

# ---------------------------------------------------------------------------
Write-Host "[10/11] validation and not-found behavior"
# ---------------------------------------------------------------------------
$badShape = Invoke-ToolWithParams "append_csv_rows" @{ fileId = $fileId; rows = ,@("a", "b") }
Assert-True ($badShape.ok -eq $false -and $badShape.error.Contains("columns")) "append with wrong column count fails with a clear message"

$unknownRead = Invoke-Tool "get_csv_content" '{"fileId":"ffffffffffffffffffffffffffffffff"}'
Assert-True ($unknownRead.ok -eq $false) "reading an unknown file returns ok:false"

$unknownStatus = 0
try {
    Invoke-WebRequest -Uri "$BaseUrl/download/ffffffffffffffffffffffffffffffff" -Method Get -UseBasicParsing | Out-Null
    $unknownStatus = 200
}
catch {
    $unknownStatus = [int]$_.Exception.Response.StatusCode
}
Assert-True ($unknownStatus -eq 404) "download endpoint returns 404 for unknown fileId (got $unknownStatus)"

# ---------------------------------------------------------------------------
Write-Host "[11/11] delete_csv_file"
# ---------------------------------------------------------------------------
$deleteResult = Invoke-Tool "delete_csv_file" ('{"fileId":"' + $fileId + '"}')
Assert-True ($deleteResult.ok -eq $true) "delete_csv_file succeeds"
$afterDeleteStatus = 0
try {
    $afterDelete = Invoke-WebRequest -Uri "$BaseUrl/download/$fileId" -Method Get -UseBasicParsing -ErrorAction Stop
    $afterDeleteStatus = $afterDelete.StatusCode
}
catch {
    $afterDeleteStatus = [int]$_.Exception.Response.StatusCode
}
Assert-True ($afterDeleteStatus -eq 404) "file is gone from the download endpoint after deletion (got $afterDeleteStatus)"

Write-Host ""
if ($failures -eq 0) {
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "$failures check(s) FAILED" -ForegroundColor Red
    exit 1
}
