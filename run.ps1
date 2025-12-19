param(
    [string]$Country = "US",
    [string]$Query = "test",
    [bool]$Headless = $false,
    [int]$ScrollRounds = 4
)

Write-Host "Running FbAdLibraryScraper with params: Country=$Country, Query=$Query, Headless=$Headless, ScrollRounds=$ScrollRounds"

dotnet run --project src/FbAdLibraryScraper -- --country $Country --query $Query --headless $Headless --scrollRounds $ScrollRounds
