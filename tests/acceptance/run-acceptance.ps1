param(
    [Parameter(Mandatory = $true)][string]$ProtoExe,
    [Parameter(Mandatory = $true)][string]$CasesFile,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$TimeoutMs = 180000
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$cases = Get-Content $CasesFile -Raw -Encoding UTF8 | ConvertFrom-Json

$results = @()
for ($i = 0; $i -lt $cases.Count; $i++) {
    $c = $cases[$i]
    $cmd = [string]$c.command
    $escaped = $cmd.Replace('"', '\"')
    $argString = ([string]$c.mode) + ' ' + ([string]$c.shell) + ' "' + $escaped + '"'

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $ProtoExe
    $psi.Arguments = $argString
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    $p = [System.Diagnostics.Process]::Start($psi)
    $mo = New-Object System.IO.MemoryStream
    $me = New-Object System.IO.MemoryStream
    $t1 = $p.StandardOutput.BaseStream.CopyToAsync($mo)
    $t2 = $p.StandardError.BaseStream.CopyToAsync($me)
    $ok = $p.WaitForExit($TimeoutMs)
    if (-not $ok) { $p.Kill(); $p.WaitForExit() }
    [void][System.Threading.Tasks.Task]::WaitAll(@($t1, $t2), 5000)

    $ob = $mo.ToArray()
    $eb = $me.ToArray()
    $idx = '{0:d2}' -f ($i + 1)
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir "case-$idx.json"), $ob)
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir "case-$idx.err"), $eb)

    $results += [pscustomobject]@{
        index    = $i + 1
        name     = [string]$c.name
        protoExit = $p.ExitCode
        outBytes = $ob.Length
        errBytes = $eb.Length
        timedOut = (-not $ok)
    }
    $p.Dispose()
}

$summaryPath = Join-Path $OutDir 'summary.json'
$results | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding UTF8
Write-Host ("cases done: " + $results.Count)
