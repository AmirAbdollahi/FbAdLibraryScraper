using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FbAdLibraryScraper.Models;

namespace FbAdLibraryScraper.Services
{
    public class ResultStorageService
    {
        private readonly string _outputDir;
        private readonly string _screenshotDir;

        public ResultStorageService(string outputDir, string screenshotDir)
        {
            _outputDir = outputDir;
            _screenshotDir = screenshotDir;
            Directory.CreateDirectory(_outputDir);
            Directory.CreateDirectory(_screenshotDir);
        }

        public async Task<string> SaveRawResponseAsync(string content, int index)
        {
            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"resp_{ts}_{index}.json";
            var path = Path.Combine(_outputDir, fileName);
            await File.WriteAllTextAsync(path, content, Encoding.UTF8);
            return path;
        }

        public async Task SaveScreenshotBytesAsync(byte[] bytes, string suffix = "screenshot")
        {
            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = $"{suffix}_{ts}.png";
            var path = Path.Combine(_screenshotDir, fileName);
            await File.WriteAllBytesAsync(path, bytes);
        }

        public async Task SaveParsedResultsAsync(IEnumerable<AdItem> items)
        {
            var jsonPath = Path.Combine(_outputDir, "results.json");
            var csvPath = Path.Combine(_outputDir, "results.csv");

            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(items, options), Encoding.UTF8);

            using (var sw = new StreamWriter(csvPath, false, Encoding.UTF8))
            {
                await sw.WriteLineAsync("ad_text,advertiser_name,start_date,ad_url");
                foreach (var it in items)
                {
                    var line = $"{EscapeCsv(it.AdText)},{EscapeCsv(it.AdvertiserName)},{EscapeCsv(it.StartDate)},{EscapeCsv(it.AdUrl)}";
                    await sw.WriteLineAsync(line);
                }
            }
        }

        private static string EscapeCsv(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var escaped = s.Replace("\"", "\"\"");
            if (escaped.Contains(",") || escaped.Contains("\"") || escaped.Contains("\n") || escaped.Contains("\r"))
                return $"\"{escaped}\"";
            return escaped;
        }
    }
}
