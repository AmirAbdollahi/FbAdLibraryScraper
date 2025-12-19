# FbAdLibraryScraper

A .NET console application that scrapes Facebook Ads Library using Playwright for browser automation.

## Prerequisites

- .NET 10 SDK
- Node.js (for Playwright browser installation)

## Setup

1. Clone the repository
2. Run `dotnet restore`
3. Install Playwright browsers: `npx playwright install`

## Usage

```bash
dotnet run --project src/FbAdLibraryScraper -- --country US --query "test" --headless false --scrollRounds 4
```

### Command Line Options

- `--country`: Country code (default: US)
- `--category`: Ad category (optional)
- `--query`: Search query (default: test)
- `--headless`: Run in headless mode (default: false)
- `--scrollRounds`: Number of scroll rounds to load more ads (default: 8)
- `--outputDir`: Output directory (default: responses)
- `--screenshotDir`: Screenshot directory (default: screenshots)

## Output

- Raw GraphQL responses saved to `responses/`
- Parsed results in `responses/results.json` and `responses/results.csv`
- Screenshots on errors saved to `screenshots/`

## Notes

- This is a proof-of-concept and may require manual intervention for CAPTCHAs.
- Do not use for production scraping; respect Facebook's terms of service.
