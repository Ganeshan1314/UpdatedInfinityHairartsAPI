using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace InfinityHairartsAPI.Modals;

public sealed class CustomerSalonQuery
{
    [StringLength(100)] public string? Search { get; set; } = "";
    [StringLength(2048)] public string? Cursor { get; set; }
    [Range(1, 50)] public int PageSize { get; set; } = 20;

    public bool TryReadCursor(out CustomerSalonCursor? after)
    {
        after = null;
        if (string.IsNullOrEmpty(Cursor)) return true;
        try
        {
            after = JsonSerializer.Deserialize<CustomerSalonCursor>(WebEncoders.Base64UrlDecode(Cursor));
            return after is not null && after.SalonMasterID != Guid.Empty
                && !string.IsNullOrWhiteSpace(after.SalonName) && after.SalonName.Length <= 200
                && after.SearchHash == CustomerSalonCursor.Fingerprint(Search);
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

public sealed record CustomerSalonCursor(
    [property: JsonPropertyName("o")] int DisplayOrder,
    [property: JsonPropertyName("n")] string SalonName,
    [property: JsonPropertyName("i")] Guid SalonMasterID,
    [property: JsonPropertyName("q")] string SearchHash)
{
    public static string Fingerprint(string? search) => WebEncoders.Base64UrlEncode(
        SHA256.HashData(Encoding.UTF8.GetBytes((search ?? "").Trim())));

    // Keep multilingual names compact enough for server query-string limits.
    public string Encode() => WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(this,
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}

public sealed class CustomerSalonCard
{
    public Guid SalonMasterID { get; set; }
    public Guid LocationMasterID { get; set; }
    public string SalonName { get; set; } = "";
    public string LocationName { get; set; } = "";
    public string SalonAddress { get; set; } = "";
    public string ContactNumber { get; set; } = "";
    [JsonIgnore] public int DisplayOrder { get; set; }
}

public sealed record CustomerSalonPage(IReadOnlyList<CustomerSalonCard> Items, string? NextCursor);
