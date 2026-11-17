# Runs each anime provider's live tests (<Plugin>Provider_Live_*) and writes the result into the README's Status column.
# Exits 1 when a provider is broken, so the run shows up as failed.
param(
    [string]$Project = "tests/Totoro.Plugins.Tests",
    [string]$Readme = "README.md"
)

$ErrorActionPreference = "Stop"

dotnet build $Project -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# The plugins come from the tests themselves, so a new provider's live test is picked up without listing it here.
$plugins = dotnet test $Project -c Release --no-build --list-tests |
    Select-String '^\s+\S*\.(\w+)Provider_Live_' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Sort-Object -Unique
if (-not $plugins) { throw "No *Provider_Live_* tests found." }

$readmeText = Get-Content $Readme -Raw
$broken = @()
foreach ($plugin in $plugins) {
    $working = $false
    # Sites have bad minutes, so a failure gets one retry before it counts.
    foreach ($attempt in 1..2) {
        $output = dotnet test $Project -c Release --no-build --filter "FullyQualifiedName~.$($plugin)Provider_Live_" | Tee-Object -Variable output
        if ($LASTEXITCODE -eq 0) { $working = $true; break }
    }

    # A bot check only a person could pass (BrowserPageLoader.BotCheck) says nothing about the plugin.
    $status = if ($working) { "✅ Working" }
              elseif ($output -match "bot check didn't pass") { "⚠️ Unverified (site's bot check)" }
              else { $broken += $plugin; "❌ Broken" }
    $row = "(?m)^(\| \*\*[^|]*\*\* \(``Totoro\.Plugins\.$plugin``\) \|.*\|) [^|]* \|\r?$"
    if ($readmeText -notmatch $row) { throw "README has no Status cell for $plugin." }
    $readmeText = $readmeText -replace $row, "`$1 $status |"
    Write-Host "$plugin`: $status"
}

$readmeText = $readmeText -replace '(?<=<!-- provider-check -->).*?(?=<!-- /provider-check -->)', (Get-Date -Format "yyyy-MM-dd")
Set-Content $Readme $readmeText -NoNewline -Encoding utf8

if ($broken) {
    Write-Host "::error::Broken: $($broken -join ', ')"
    exit 1
}
