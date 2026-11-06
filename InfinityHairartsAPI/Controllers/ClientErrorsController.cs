using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InfinityHairartsAPI.Controllers;

[ApiController, Route("api/ClientErrors"), EnableRateLimiting("ClientErrors")]
[RequestSizeLimit(65536)]
public sealed class ClientErrorsController(ILogger<ClientErrorsController> logger) : ControllerBase
{
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal)
    {
        "/", "/login", "/signup", "/dashboard", "/home", "/privacy", "/privacy-policy", "/privacy-notice",
        "/bookappoinment", "/customertimeseatselection", "/cart", "/registration", "/mybooking"
    };

    [HttpPost]
    public IActionResult Post(Batch batch)
    {
        if (batch.Reports.Any(report => report is null)) return BadRequest();
        foreach (var report in batch.Reports)
        {
            // Never accept arbitrary exception messages, form values, URLs or stacks
            // into the server log. Both apps submit the same bounded metadata format.
            var endpoint = Match(report.Endpoint, @"^/(?:Admin|Appoinment|Login|Location|Notifications|SuperAdmin)/[A-Za-z][A-Za-z0-9_-]{0,64}$");
            var frames = Regex.Matches(report.Stack ?? "", @"[A-Za-z0-9._-]{1,100}\.js:\d{1,9}:\d{1,9}")
                .Cast<Match>().Take(12).Select(frame => frame.Value);
            logger.LogWarning("Client error {ReportID} at {OccurredAt}: application={Application}, category={Category}, page={Page}, endpoint={Endpoint}, method={Method}, status={Status}, type={ErrorType}, code={Code}, platform={Platform}, frames={Frames}",
                report.ReportID, report.OccurredAt, batch.Application == "SalonexaAdmin" ? "SalonexaAdmin" : "Customer",
                Match(report.Category, "^(runtime|promise|http|api)$"),
                report.Page is not null && Pages.Contains(report.Page) ? report.Page : "/", endpoint,
                Match(report.Method, "^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)$"), report.Status,
                Match(report.ErrorType, "^[A-Za-z]{1,40}$"), Match(report.Code, "^NG[0-9]{4,5}$"),
                Match(report.Platform, "^(android|ios|web)$"), string.Join(";", frames));
        }
        return NoContent();
    }

    private static string Match(string? value, string pattern)
    {
        if (value is null) return "";
        // Return the matched text, never an unchecked trailing newline ($ accepts one).
        var match = Regex.Match(value, pattern, RegexOptions.CultureInvariant);
        return match.Success ? match.Value : "";
    }

    public sealed class Batch
    {
        [StringLength(40)] public string? Application { get; set; }
        [Required, MinLength(1), MaxLength(10)] public List<Report> Reports { get; set; } = [];
    }

    public sealed class Report
    {
        [Required, InfinityHairartsAPI.Modals.NonEmptyGuid] public Guid ReportID { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        [StringLength(16)] public string? Category { get; set; }
        [StringLength(100)] public string? Page { get; set; }
        [StringLength(200)] public string? Endpoint { get; set; }
        [StringLength(10)] public string? Method { get; set; }
        [Range(0, 599)] public int Status { get; set; }
        [StringLength(40)] public string? ErrorType { get; set; }
        [StringLength(16)] public string? Code { get; set; }
        [StringLength(1500)] public string? Stack { get; set; }
        [StringLength(16)] public string? Platform { get; set; }
    }
}
