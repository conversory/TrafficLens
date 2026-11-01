$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$lock = Join-Path $project 'packages.lock.json'
$original = [IO.File]::ReadAllBytes($lock)
$before = Get-Content -LiteralPath $lock -Raw | ConvertFrom-Json -AsHashtable
try {
    & dotnet restore (Join-Path $project 'TrafficLens.csproj') --force-evaluate
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed' }
    $after = Get-Content -LiteralPath $lock -Raw | ConvertFrom-Json -AsHashtable
    # The single-file analyzer is selected by the SDK, not an application dependency.
    foreach ($graph in @($before, $after)) {
        foreach ($framework in $graph.dependencies.Values) {
            $framework.Remove('Microsoft.NET.ILLink.Tasks') | Out-Null
        }
    }
    function Canonical($value) {
        if ($value -is [System.Collections.IDictionary]) {
            $sorted = [ordered]@{}
            foreach ($key in ($value.Keys | Sort-Object)) { $sorted[$key] = Canonical $value[$key] }
            return $sorted
        }
        return $value
    }
    $expected = Canonical $before | ConvertTo-Json -Depth 50 -Compress
    $actual = Canonical $after | ConvertTo-Json -Depth 50 -Compress
    if ($expected -cne $actual) { throw 'Application dependencies changed. Review and update packages.lock.json explicitly.' }
    Write-Output 'Application dependency versions and hashes verified; SDK analyzer may follow the installed .NET SDK.'
}
finally {
    [IO.File]::WriteAllBytes($lock, $original)
}
