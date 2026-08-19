using System.Net;
using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Anamnys.Api.Controllers;

public class ContactOptions
{
    /// <summary>Where new-submission notification emails are sent. Not a secret — fine in
    /// appsettings.*.json.</summary>
    public string SupportToAddress { get; set; } = "support@clinicaldraft.ai";
}

/// <summary>
/// Public "Contact / Support" form submission — unauthenticated (anyone reaching the marketing
/// site can use it, not just signed-in providers), unlike every other controller here.
/// </summary>
[ApiController]
[Route("api/contact")]
public class ContactController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEmailService _email;
    private readonly ContactOptions _opts;
    private readonly ILogger<ContactController> _logger;

    public ContactController(
        AppDbContext db, IEmailService email, IOptions<ContactOptions> opts, ILogger<ContactController> logger)
    {
        _db = db;
        _email = email;
        _opts = opts.Value;
        _logger = logger;
    }

    /// <summary>
    /// Persists a contact-form submission and best-effort emails a notification to the support
    /// inbox. The record is the source of truth: if the notification email fails to send (bad
    /// API key, Resend outage, etc.), we log it and still return success — the submission is
    /// safely stored and can be found later, rather than the visitor seeing a false error for
    /// an email-delivery problem that isn't theirs.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] ContactRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullName) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "Full name, email, and message are required." });

        var message = new ContactMessage
        {
            FullName = request.FullName.Trim(),
            Email = request.Email.Trim(),
            Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim(),
            Message = request.Message.Trim(),
        };

        _db.ContactMessages.Add(message);
        await _db.SaveChangesAsync();

        try
        {
            await _email.SendAsync(
                _opts.SupportToAddress,
                $"New contact form submission from {message.FullName}",
                BuildNotificationHtml(message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send contact-form notification email for ContactMessage {Id}", message.Id);
        }

        return NoContent();
    }

    private static string BuildNotificationHtml(ContactMessage m) =>
        $"""
        <p><strong>Name:</strong> {WebUtility.HtmlEncode(m.FullName)}</p>
        <p><strong>Email:</strong> {WebUtility.HtmlEncode(m.Email)}</p>
        <p><strong>Phone:</strong> {WebUtility.HtmlEncode(m.Phone ?? "—")}</p>
        <p><strong>Message:</strong></p>
        <p>{WebUtility.HtmlEncode(m.Message).Replace("\n", "<br/>")}</p>
        """;
}

public record ContactRequest(string FullName, string Email, string? Phone, string Message);
