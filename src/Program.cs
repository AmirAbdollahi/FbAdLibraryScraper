using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FbAdLibraryScraper.Scraper;
using FbAdLibraryScraper.Services;
using Microsoft.Extensions.Configuration;

namespace FbAdLibraryScraper
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            Console.WriteLine($"[info] FbAdLibraryScraper starting at {DateTime.UtcNow:O}");

            // load config (appsettings.json optional)
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddCommandLine(args)
                .Build();

            var startUrl = config["startUrl"] ?? "https://www.facebook.com/ads/library/";
            var country = config["country"] ?? config["Country"] ?? "US";
            var category = config["category"] ?? config["Category"] ?? "";
            var query = config["query"] ?? config["SearchKeyword"] ?? "";
            var headless = bool.TryParse(config["headless"], out var h) ? h : false;
            var scrollRounds = int.TryParse(config["scrollRounds"], out var sr) ? sr : 8;
            var outputDir = config["outputDir"] ?? config["OutputFolder"] ?? Path.Combine(Directory.GetCurrentDirectory(), "responses");
            var screenshotDir = config["screenshotDir"] ?? Path.Combine(Directory.GetCurrentDirectory(), "screenshots");

            Console.WriteLine($"[info] Config: startUrl={startUrl}, country={country}, category={category}, query={query}, headless={headless}, scrollRounds={scrollRounds}");
            Console.WriteLine($"[info] Output: {outputDir}");

            // Ensure Playwright browsers installed - instructive message (do not install here)
            Console.WriteLine("[note] Make sure Playwright browsers are installed: run `playwright install` or `npx playwright install` once before running.");

            try
            {
                Directory.CreateDirectory(outputDir);
                Directory.CreateDirectory(screenshotDir);

                var storage = new ResultStorageService(outputDir, screenshotDir);
                var scraper = new AdsLibraryScraper(startUrl, country, category, query, headless, scrollRounds, storage);

                var parsed = await scraper.RunAsync();

                Console.WriteLine($"[info] Done. Parsed items: {parsed?.Count ?? 0}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[error] Unhandled exception: {ex}");
                return 1;
            }
        }
    }
}
