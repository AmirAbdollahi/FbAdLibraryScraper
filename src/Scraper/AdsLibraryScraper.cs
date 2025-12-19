using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using FbAdLibraryScraper.Models;
using FbAdLibraryScraper.Services;

namespace FbAdLibraryScraper.Scraper
{
    public class AdsLibraryScraper
    {
        private readonly string _startUrl;
        private readonly string _country;
        private readonly string _category;
        private readonly string _query;
        private readonly bool _headless;
        private readonly int _scrollRounds;
        private readonly ResultStorageService _storage;

        private int _respIndex = 0;
        private readonly List<string> _savedResponsePaths = new();

        public AdsLibraryScraper(string startUrl, string country, string category, string query, bool headless, int scrollRounds, ResultStorageService storage)
        {
            _startUrl = startUrl;
            _country = country;
            _category = category;
            _query = query;
            _headless = headless;
            _scrollRounds = scrollRounds;
            _storage = storage;
        }

        public async Task<List<AdItem>> RunAsync()
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36"
            });

            var page = await context.NewPageAsync();

            // Listen for network responses and persist candidate GraphQL/XHR payloads
            page.Response += async (_, response) =>
            {
                try
                {
                    if (IsAdsGraphQlResponse(response))
                    {
                        var text = await response.TextAsync();
                        var path = await _storage.SaveRawResponseAsync(text, Interlocked.Increment(ref _respIndex));
                        _savedResponsePaths.Add(path);
                        Console.WriteLine($"[info] saved response -> {Path.GetFileName(path)}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[warn] response handler error: {ex.Message}");
                }
            };

            try
            {
                // Append simple query params (may or may not pre-fill)
                var url = BuildUrlWithParams(_startUrl, _country, _category, _query);
                Console.WriteLine($"[info] Navigating to: {url}");
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 45000 });

                // If page requires human solving (captcha) the user can do it headful
                if (!_headless)
                {
                    Console.WriteLine("[info] Running headful — if a CAPTCHA appears solve it manually in the browser.");
                }

                // Try to fill search input if present (best-effort)
                try
                {
                    if (!string.IsNullOrEmpty(_query))
                    {
                        var searchSelectors = new[] { "input[type='search']", "input[placeholder*='Search']", "input[aria-label='Search']", "input[role='combobox']" };
                        foreach (var sel in searchSelectors)
                        {
                            var el = await page.QuerySelectorAsync(sel);
                            if (el != null)
                            {
                                await el.FillAsync(_query);
                                try { await el.PressAsync("Enter"); } catch { }
                                Console.WriteLine($"[info] Filled search input using selector {sel}");
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[warn] filling search input failed: {ex.Message}");
                }

                // Trigger scrolling to load more data and capture GraphQL responses
                for (int i = 0; i < _scrollRounds; i++)
                {
                    Console.WriteLine($"[info] scroll round {i + 1}/{_scrollRounds}");
                    await page.EvaluateAsync("() => { window.scrollBy(0, window.innerHeight * 2); }");

                    try
                    {
                        // Wait for at least one GraphQL/XHR response within timeout
                        var resp = await page.WaitForResponseAsync(r => IsAdsGraphQlResponse(r), new PageWaitForResponseOptions { Timeout = 15000 });
                        Console.WriteLine($"[info] observed network response: {resp.Url}");
                    }
                    catch (TimeoutException)
                    {
                        Console.WriteLine("[info] no network response in this scroll interval");
                    }

                    await Task.Delay(1200);
                }

                // Wait briefly for final responses
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
                Console.WriteLine("[info] finished scrolling, parsing collected payloads...");

                // Parse saved files (heuristic)
                var parsed = new List<AdItem>();
                foreach (var path in _savedResponsePaths)
                {
                    try
                    {
                        var txt = await File.ReadAllTextAsync(path);
                        using var doc = JsonDocument.Parse(txt);
                        var items = ExtractAdItemsFromGraphQl(doc.RootElement);
                        if (items?.Count > 0) parsed.AddRange(items);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[warn] parse error for {Path.GetFileName(path)}: {ex.Message}");
                    }
                }

                // dedupe simple
                var deduped = parsed
                    .GroupBy(a => (a.AdText ?? "") + "||" + (a.AdvertiserName ?? ""))
                    .Select(g => g.First())
                    .ToList();

                Console.WriteLine($"[info] parsed {parsed.Count} items -> {deduped.Count} deduped");

                // Save parsed results
                await _storage.SaveParsedResultsAsync(deduped);

                return deduped;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[error] scraping run failed: {ex.Message}");
                // try screenshot
                try
                {
                    var shot = await page.ScreenshotAsync();
                    await _storage.SaveScreenshotBytesAsync(shot, "error");
                    Console.WriteLine("[info] screenshot saved after error");
                }
                catch { }
                throw;
            }
            finally
            {
                try { await page.CloseAsync(); } catch { }
                try { await context.CloseAsync(); } catch { }
                try { await browser.CloseAsync(); } catch { }
            }
        }

        private static string BuildUrlWithParams(string baseUrl, string country, string category, string query)
        {
            var qp = new List<string>();
            if (!string.IsNullOrEmpty(country)) qp.Add($"country={Uri.EscapeDataString(country)}");
            if (!string.IsNullOrEmpty(category)) qp.Add($"ad_type={Uri.EscapeDataString(category)}");
            if (!string.IsNullOrEmpty(query)) qp.Add($"search_terms={Uri.EscapeDataString(query)}");
            return qp.Count == 0 ? baseUrl : $"{baseUrl.TrimEnd('/')}/?{string.Join("&", qp)}";
        }

        private static bool IsAdsGraphQlResponse(IResponse response)
        {
            try
            {
                if (response.Url.Contains("/api/graphql", StringComparison.OrdinalIgnoreCase)) return true;
                var req = response.Request;
                if (req != null && req.Headers != null && req.Headers.TryGetValue("x-fb-friendly-name", out var h))
                {
                    if (!string.IsNullOrEmpty(h) && h.Contains("AdLibrarySearchPaginationQuery")) return true;
                }
            }
            catch { }
            return false;
        }

        // Heuristic extractor - same idea as before (keeps it resilient)
        private static List<AdItem> ExtractAdItemsFromGraphQl(JsonElement root)
        {
            var results = new List<AdItem>();

            void Recurse(JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (TryBuildAdFromElement(el, out var ad))
                    {
                        results.Add(ad);
                    }
                    else
                    {
                        foreach (var p in el.EnumerateObject()) Recurse(p.Value);
                    }
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in el.EnumerateArray()) Recurse(it);
                }
            }

            bool TryBuildAdFromElement(JsonElement el, out AdItem item)
            {
                item = new AdItem();

                string? TryGet(JsonElement e, params string[] names)
                {
                    if (e.ValueKind != JsonValueKind.Object) return null;
                    foreach (var n in names)
                    {
                        if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString();
                    }
                    return null;
                }

                var adText = TryGet(el, "bodyText", "body", "message", "text", "ad_text");
                var advertiser = TryGet(el, "advertiserName", "pageName", "publisher", "advertiser");
                var startDate = TryGet(el, "startDate", "start_date", "creation_time");
                var url = TryGet(el, "ad_url", "url", "link");

                if (string.IsNullOrEmpty(adText) && el.TryGetProperty("creative", out var creative))
                {
                    adText = TryGet(creative, "bodyText", "body", "message", "text");
                }

                if (string.IsNullOrEmpty(adText) && string.IsNullOrEmpty(advertiser))
                    return false;

                item.AdText = adText;
                item.AdvertiserName = advertiser;
                item.StartDate = startDate;
                item.AdUrl = url;
                return true;
            }

            Recurse(root);
            return results;
        }
    }
}
