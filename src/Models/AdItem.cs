using System.Text.Json.Serialization;

namespace FbAdLibraryScraper.Models;

public class AdItem
{
    public string? AdText { get; set; }
    public string? AdvertiserName { get; set; }
    public string? StartDate { get; set; }
    public string? AdUrl { get; set; }
}
