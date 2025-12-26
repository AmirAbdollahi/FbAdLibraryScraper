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
            // For demo: use SlowMo only when running headful so human can observe UI
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _headless,
                SlowMo = _headless ? 0 : 100
            });

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36"
            });

            var page = await context.NewPageAsync();

            // Prepare debug log file
            var debugPath = Path.Combine(_storage.OutputDir, "debug_requests.log");
            try { Directory.CreateDirectory(_storage.OutputDir); } catch { }

            File.WriteAllText(debugPath, $"DEBUG START {DateTime.UtcNow:O}\n");

            int debugCount = 0;
            bool firstDataResponseReceived = false;

            // Log requests
            page.Request += (_, req) =>
            {
                try
                {
                    if (Interlocked.Increment(ref debugCount) <= 1000)
                    {
                        var line = $"REQ {DateTime.UtcNow:O} {req.Method} {req.Url}";
                        File.AppendAllText(debugPath, line + Environment.NewLine);
                    }
                }
                catch { }
            };

            // Log responses and optionally store JSON snippets
            page.Response += async (_, resp) =>
            {
                try
                {
                    if (Volatile.Read(ref debugCount) <= 1000)
                    {
                        var line = $"RESP {DateTime.UtcNow:O} {resp.Status} {resp.Url}";
                        File.AppendAllText(debugPath, line + Environment.NewLine);

                        if (resp.Headers != null && resp.Headers.TryGetValue("content-type", out var ct) && ct != null && ct.Contains("json", StringComparison.OrdinalIgnoreCase))
                        {
                            string? text = null;
                            try { text = await resp.TextAsync(); } catch { }
                            if (!string.IsNullOrEmpty(text))
                            {
                                var snippet = text.Length > 2000 ? text.Substring(0, 2000) : text;
                                File.AppendAllText(debugPath, "BODY_SNIPPET_START\n" + snippet + "\nBODY_SNIPPET_END\n");
                            }
                        }
                    }
                }
                catch { }
            };

            // Save full responses for ads library or graphql
            page.Response += async (_, response) =>
            {
                try
                {
                    if (IsAdsLibraryResponse(response))
                    {
                        var txt = await response.TextAsync();
                        var saved = await _storage.SaveRawResponseAsync(txt, Interlocked.Increment(ref _respIndex));
                        _savedResponsePaths.Add(saved);
                        Console.WriteLine($"[info] saved response -> {Path.GetFileName(saved)}");
                        firstDataResponseReceived = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[warn] response handler error: {ex.Message}");
                }
            };

            try
            {
                Console.WriteLine($"[info] Navigating to: {_startUrl}");
                await page.GotoAsync(_startUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

                // Wait for page readiness: visible text "Search ads"
                await page.GetByText("Search ads", new() { Exact = true }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
                Console.WriteLine("[info] Page ready: 'Search ads' visible");

                // Mandatory first interaction: focus the search input
                var focusSuccess = await FocusSearchInputAsync(page);
                if (!focusSuccess)
                {
                    await TakeScreenshotAndLogDomAsync(page, "search_input_focus_fail");
                    throw new Exception("Failed to focus search input");
                }

                // Best-effort close common overlays/consent banners
                await TryCloseOverlaysAsync(page);

                if (!_headless)
                {
                    Console.WriteLine("[info] Running headful — if a CAPTCHA appears solve it manually in the browser.");
                }

                // Click country dropdown and select "United States"
                var countrySuccess = await SelectCountryAsync(page);
                Console.WriteLine($"[info] SelectCountryAsync -> {countrySuccess}");
                if (!countrySuccess)
                {
                    await TakeScreenshotAndLogDomAsync(page, "country_select_fail");
                    throw new Exception("Failed to select country");
                }

                // Click category dropdown and select "All ads"
                var catSuccess = await SelectCategoryAsync(page);
                Console.WriteLine($"[info] SelectCategoryAsync -> {catSuccess}");
                if (!catSuccess)
                {
                    await TakeScreenshotAndLogDomAsync(page, "category_select_fail");
                    throw new Exception("Failed to select category");
                }

                // Fill search input and press enter
                if (!string.IsNullOrEmpty(_query))
                {
                    var searchSuccess = await FillSearchAndSubmitAsync(page, _query);
                    Console.WriteLine($"[info] FillSearchAndSubmitAsync({_query}) -> {searchSuccess}");
                    if (!searchSuccess)
                    {
                        await TakeScreenshotAndLogDomAsync(page, "search_fill_fail");
                        throw new Exception("Failed to fill search");
                    }
                }

                // Wait for first data response
                Console.WriteLine("[info] Waiting for first ads library response...");
                var firstResp = await page.WaitForResponseAsync(r => IsAdsLibraryResponse(r), new PageWaitForResponseOptions { Timeout = 30000 });
                Console.WriteLine($"[info] First data response received: {firstResp.Url}");

                // Now scroll to load more
                for (int i = 0; i < Math.Max(1, _scrollRounds); i++)
                {
                    Console.WriteLine($"[info] scroll round {i + 1}/{_scrollRounds}");
                    await page.EvaluateAsync("() => window.scrollBy(0, window.innerHeight * 2)");
                    await Task.Delay(2000);
                }

                // Wait for final network idle
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
                Console.WriteLine("[info] finished scrolling, parsing collected payloads...");

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

                var deduped = parsed
                    .GroupBy(a => (a.AdText ?? "") + "||" + (a.AdvertiserName ?? ""))
                    .Select(g => g.First())
                    .ToList();

                Console.WriteLine($"[info] parsed {parsed.Count} items -> {deduped.Count} deduped");

                await _storage.SaveParsedResultsAsync(deduped);

                // Take success screenshot
                await TakeScreenshotAsync(page, "success");

                return deduped;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[error] scraping run failed: {ex.Message}");
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

        private static bool IsAdsLibraryResponse(IResponse response)
        {
            try
            {
                if (response.Url.Contains("/ads/library/", StringComparison.OrdinalIgnoreCase) &&
                    response.Headers.TryGetValue("content-type", out var ct) &&
                    ct.Contains("json", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            return false;
        }

        private async Task<bool> FocusSearchInputAsync(IPage page)
        {
            try
            {
                // Try strategies in order, pick the first visible one
                ILocator? searchInput = null;

                // a) input[placeholder*="Search"]
                var candidates = page.Locator("input[placeholder*=\"Search\"]");
                var count = await candidates.CountAsync();
                for (int i = 0; i < count; i++)
                {
                    var cand = candidates.Nth(i);
                    if (await cand.IsVisibleAsync())
                    {
                        searchInput = cand;
                        Console.WriteLine("[info] Found search input by placeholder");
                        break;
                    }
                }

                if (searchInput == null)
                {
                    // b) input[aria-label*="Search"]
                    candidates = page.Locator("input[aria-label*=\"Search\"]");
                    count = await candidates.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var cand = candidates.Nth(i);
                        if (await cand.IsVisibleAsync())
                        {
                            searchInput = cand;
                            Console.WriteLine("[info] Found search input by aria-label");
                            break;
                        }
                    }
                }

                if (searchInput == null)
                {
                    // c) role="combobox"
                    candidates = page.Locator("[role=\"combobox\"]");
                    count = await candidates.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var cand = candidates.Nth(i);
                        if (await cand.IsVisibleAsync())
                        {
                            searchInput = cand;
                            Console.WriteLine("[info] Found search input by role combobox");
                            break;
                        }
                    }
                }

                if (searchInput == null)
                {
                    // d) the only visible input element with width > 200px
                    var inputs = page.Locator("input");
                    count = await inputs.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var input = inputs.Nth(i);
                        var isVisible = await input.IsVisibleAsync();
                        if (isVisible)
                        {
                            var boundingBox = await input.BoundingBoxAsync();
                            if (boundingBox != null && boundingBox.Width > 200)
                            {
                                searchInput = input;
                                Console.WriteLine("[info] Found search input by visible width > 200px");
                                break;
                            }
                        }
                    }
                }

                if (searchInput == null)
                {
                    return false;
                }

                // Scroll into view
                await searchInput.ScrollIntoViewIfNeededAsync();

                // Click the input
                await searchInput.ClickAsync();

                // Assert that document.activeElement === input
                var isFocused = await page.EvaluateAsync<bool>(@"(locator) => {
                    return document.activeElement === locator;
                }", await searchInput.ElementHandleAsync());

                if (isFocused)
                {
                    Console.WriteLine("[info] Search input focused successfully");
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] FocusSearchInputAsync failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SelectCountryAsync(IPage page)
        {
            try
            {
                // Click the country dropdown trigger (button, div with role=button, or div with text "United States")
                await page.Locator("button, [role='button'], div").Filter(new() { HasText = "United States" }).First.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                Console.WriteLine("[info] Clicked country dropdown trigger");

                // Wait for the dropdown to open (assume it opens after click)
                await Task.Delay(500); // Allow dropdown to open
                Console.WriteLine("[info] Country dropdown opened");

                // Send ArrowDown to activate keyboard-navigation mode
                await page.Keyboard.PressAsync("ArrowDown");
                Console.WriteLine("[info] ArrowDown sent to activate keyboard-navigation mode");

                // Send keyboard Tab presses in a loop until the input receives focus
                var maxTabs = 8;
                var inputFocused = false;

                for (int tabCount = 1; tabCount <= maxTabs; tabCount++)
                {
                    await page.Keyboard.PressAsync("Tab");

                    // Check if activeElement is INPUT with placeholder containing "Search"
                    var activeElementInfo = await page.EvaluateAsync<(string tagName, string placeholder)>(@"() => {
                        const el = document.activeElement;
                        return {
                            tagName: el ? el.tagName : '',
                            placeholder: el ? el.getAttribute('placeholder') || '' : ''
                        };
                    }");

                    Console.WriteLine($"[info] Tab sent ({tabCount}), active element tag: {activeElementInfo.tagName}, placeholder: {activeElementInfo.placeholder}");

                    if (activeElementInfo.tagName == "INPUT" && activeElementInfo.placeholder.Contains("Search", StringComparison.OrdinalIgnoreCase))
                    {
                        inputFocused = true;
                        Console.WriteLine("[info] Dropdown search input focused via keyboard");
                        break;
                    }
                }

                if (!inputFocused)
                {
                    await TakeScreenshotAndLogDomAsync(page, "country_keyboard_navigation_failed");
                    throw new Exception("Keyboard navigation did not reach dropdown search input");
                }

                // Type the country name exactly: "United States"
                await page.Keyboard.TypeAsync("United States");
                Console.WriteLine("[info] Typed 'United States' into search textbox");

                // Wait for dropdown list to visually filter
                await Task.Delay(1000);

                // Verify that the dropdown list contents change (e.g. number of visible options decreases or matching text appears)
                var listChanged = await page.EvaluateAsync<bool>(@"() => {
                    const options = document.querySelectorAll('[role=""option""]');
                    const visibleOptions = Array.from(options).filter(opt => opt.offsetParent !== null);
                    // Check if ""United States"" is among visible options or list is filtered
                    const hasUnitedStates = Array.from(visibleOptions).some(opt => opt.textContent.includes('United States'));
                    return hasUnitedStates || visibleOptions.length < 10; // Assume filtering if less than 10 options
                }");

                if (!listChanged)
                {
                    await TakeScreenshotAndLogDomAsync(page, "country_list_not_filtered");
                    throw new Exception("Dropdown list did not filter after typing");
                }

                Console.WriteLine("[info] Dropdown list filtered successfully");

                // Selection phase: press Enter
                await page.Keyboard.PressAsync("Enter");
                Console.WriteLine("[info] Pressed Enter to select country");

                // Assert success: dropdown closes
                var success = await page.EvaluateAsync<bool>(@"() => {
                    const listbox = document.querySelector('[role=""listbox""]');
                    return !listbox || listbox.offsetParent === null;
                }");

                if (success)
                {
                    Console.WriteLine("[info] Country selection successful");
                    await Task.Delay(500);
                    return true;
                }
                else
                {
                    await TakeScreenshotAndLogDomAsync(page, "country_enter_select_fail");
                    throw new Exception("Enter did not select the country");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] SelectCountryAsync failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> SelectCategoryAsync(IPage page)
        {
            try
            {
                // Click the category dropdown trigger (button, div with role=button, or div with text "All ads")
                await page.Locator("button, [role='button'], div").Filter(new() { HasText = "All ads" }).First.ClickAsync(new LocatorClickOptions { Timeout = 5000 });
                Console.WriteLine("[info] Clicked category dropdown trigger");

                // Wait for the dropdown to open (assume it opens after click)
                await Task.Delay(500); // Allow dropdown to open
                Console.WriteLine("[info] Category dropdown opened");

                // Keyboard navigation: send Tab to focus the first selectable option
                await page.Keyboard.PressAsync("Tab");
                Console.WriteLine("[info] Sent Tab to focus first option in dropdown");

                // Assert focus is on an option inside the dropdown
                var isOptionFocused = await page.EvaluateAsync<bool>(@"() => {
                    const el = document.activeElement;
                    if (!el) return false;
                    // Check if element has role=""option"" or is a focusable list item
                    const role = el.getAttribute('role');
                    if (role === 'option') return true;
                    // Check if inside dropdown container or any element that is not the main search input
                    let parent = el.parentElement;
                    while (parent) {
                        if (parent.getAttribute('role') === 'listbox' || parent.classList.contains('dropdown') || parent.classList.contains('menu')) {
                            return true;
                        }
                        parent = parent.parentElement;
                    }
                    // If focus moved from the main search input, assume it's in the dropdown
                    const mainSearch = document.querySelector('[role=""combobox""]');
                    return el !== mainSearch;
                }");

                if (!isOptionFocused)
                {
                    await TakeScreenshotAndLogDomAsync(page, "category_tab_focus_fail");
                    throw new Exception("Tab did not focus an option in the dropdown");
                }

                Console.WriteLine("[info] Focus confirmed on dropdown option");

                // Selection logic
                if (string.IsNullOrEmpty(_category))
                {
                    // Press Enter immediately to select default category (e.g. "All ads")
                    await page.Keyboard.PressAsync("Enter");
                    Console.WriteLine("[info] Pressed Enter to select default category");
                }
                else
                {
                    // Type category name directly (Facebook jumps to matching option)
                    await page.Keyboard.TypeAsync(_category);
                    Console.WriteLine($"[info] Typed '{_category}' to jump to matching option");
                    await Task.Delay(500);
                    await page.Keyboard.PressAsync("Enter");
                    Console.WriteLine("[info] Pressed Enter to select category");
                }

                // Assert success: dropdown closes
                var success = await page.EvaluateAsync<bool>(@"() => {
                    const listbox = document.querySelector('[role=""listbox""]');
                    return !listbox || listbox.offsetParent === null;
                }");

                if (success)
                {
                    Console.WriteLine("[info] Category selection successful");
                    await Task.Delay(500);
                    return true;
                }
                else
                {
                    await TakeScreenshotAndLogDomAsync(page, "category_enter_select_fail");
                    throw new Exception("Enter did not select the category");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] SelectCategoryAsync failed: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> FillSearchAndSubmitAsync(IPage page, string query)
        {
            try
            {
                // Use the same logic as FocusSearchInputAsync to find the search input
                ILocator? searchInput = null;

                // a) input[placeholder*="Search"]
                var candidates = page.Locator("input[placeholder*=\"Search\"]");
                var count = await candidates.CountAsync();
                for (int i = 0; i < count; i++)
                {
                    var cand = candidates.Nth(i);
                    if (await cand.IsVisibleAsync())
                    {
                        searchInput = cand;
                        Console.WriteLine("[info] Found search input for filling by placeholder");
                        break;
                    }
                }

                if (searchInput == null)
                {
                    // b) input[aria-label*="Search"]
                    candidates = page.Locator("input[aria-label*=\"Search\"]");
                    count = await candidates.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var cand = candidates.Nth(i);
                        if (await cand.IsVisibleAsync())
                        {
                            searchInput = cand;
                            Console.WriteLine("[info] Found search input for filling by aria-label");
                            break;
                        }
                    }
                }

                if (searchInput == null)
                {
                    // c) role="combobox"
                    candidates = page.Locator("[role=\"combobox\"]");
                    count = await candidates.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var cand = candidates.Nth(i);
                        if (await cand.IsVisibleAsync())
                        {
                            searchInput = cand;
                            Console.WriteLine("[info] Found search input for filling by role combobox");
                            break;
                        }
                    }
                }

                if (searchInput == null)
                {
                    // d) the only visible input element with width > 200px
                    var inputs = page.Locator("input");
                    count = await inputs.CountAsync();
                    for (int i = 0; i < count; i++)
                    {
                        var input = inputs.Nth(i);
                        var isVisible = await input.IsVisibleAsync();
                        if (isVisible)
                        {
                            var boundingBox = await input.BoundingBoxAsync();
                            if (boundingBox != null && boundingBox.Width > 200)
                            {
                                searchInput = input;
                                Console.WriteLine("[info] Found search input for filling by visible width > 200px");
                                break;
                            }
                        }
                    }
                }

                if (searchInput == null)
                {
                    return false;
                }

                await searchInput.ClickAsync();
                // Type keyword slowly (50–100ms per char)
                foreach (var c in query)
                {
                    await page.Keyboard.TypeAsync(c.ToString());
                    await Task.Delay(Random.Shared.Next(50, 101));
                }
                await page.Keyboard.PressAsync("Enter");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] FillSearchAndSubmitAsync failed: {ex.Message}");
                return false;
            }
        }

        private async Task TakeScreenshotAsync(IPage page, string suffix)
        {
            try
            {
                var shot = await page.ScreenshotAsync();
                await _storage.SaveScreenshotBytesAsync(shot, suffix);
                Console.WriteLine($"[info] Screenshot saved: {suffix}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] TakeScreenshotAsync failed: {ex.Message}");
            }
        }

        private async Task TakeScreenshotAndLogDomAsync(IPage page, string suffix)
        {
            await TakeScreenshotAsync(page, suffix);
            try
            {
                var dom = await page.EvaluateAsync<string>("() => document.body.outerHTML");
                var domPath = Path.Combine(_storage.OutputDir, $"{suffix}_dom.html");
                await File.WriteAllTextAsync(domPath, dom);
                Console.WriteLine($"[info] DOM snapshot saved: {suffix}_dom.html");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[warn] DOM snapshot failed: {ex.Message}");
            }
        }

        private static string MapCountryCodeToName(string code)
        {
            return code.ToUpper() switch
            {
                "US" => "United States",
                "CA" => "Canada",
                "GB" => "United Kingdom",
                "AU" => "Australia",
                _ => code // fallback
            };
        }

        private async Task TryCloseOverlaysAsync(IPage page)
        {
            try
            {
                // close common cookie/consent dialogs by clicking obvious buttons
                var clicked = false;
                var candidateTexts = new[] { "Accept", "I accept", "Agree", "Got it", "OK", "Close" };
                foreach (var t in candidateTexts)
                {
                    try { await page.ClickAsync($"text=\"{t}\"", new PageClickOptions { Timeout = 1500 }); clicked = true; break; } catch { }
                }

                if (!clicked)
                {
                    // JS remove very large overlays if present (best-effort, not bypassing protections)
                    await page.EvaluateAsync(@"() => {
                        const overlays = Array.from(document.querySelectorAll('div')).filter(d => {
                            try {
                                const s = window.getComputedStyle(d);
                                return s.position === 'fixed' && s.zIndex && parseInt(s.zIndex) > 1000 && (d.clientHeight > 50 || d.clientWidth > 50);
                            } catch { return false; }
                        });
                        for (const o of overlays) { o.style.display = 'none'; }
                        return overlays.length > 0;
                    }");
                }
            }
            catch { }
        }

        /// <summary>
        /// Heuristic extractor - look for ad-like objects in GraphQL / JSON payloads.
        /// </summary>
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

            Recurse(root);
            return results;
        }

        private static bool TryBuildAdFromElement(JsonElement el, out AdItem item)
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
    }
}
