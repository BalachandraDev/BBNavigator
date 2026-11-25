using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FlowMind.Domain.Entities;
using FlowMind.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace FlowMind.Infrastructure.Insights;

public sealed class SupportRejectedException : Exception
{
    public int StatusCode { get; }
    public SupportRejectedException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
}

/// <summary>
/// "Send this to support". The question is read from the sender's own stored chat (never typed in by the sender), so the inbox cannot be
/// filled with invented questions; the sender adds only an optional note and contact. Capped per sender per day and per company.
/// </summary>
public sealed class SupportService
{
    public const int DailyPerSender = 5, MaxOpenPerCompany = 500, MaxNote = 500, MaxContact = 120, MaxQuestion = 1000;
    private readonly FlowMindDbContext _db;
    public SupportService(FlowMindDbContext db) { _db = db; }

    private static string Clean(string? s, int max)
    {
        var t = Regex.Replace(s ?? "", @"[\p{C}-[\r\n\t]]", " ");
        t = Regex.Replace(t, @"[ \t]+", " ").Trim();
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    /// <param name="ownerId">Chat owner id of the sender (the signed-in user, or the share link plus the visitor). The chat must belong to it.</param>
    public async Task<SupportRequest> CreateAsync(Guid tenantId, string channel, string ownerId, Guid conversationId, string? note, string? contact, CancellationToken ct)
    {
        var convo = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.OwnerId == ownerId, ct)
            ?? throw new SupportRejectedException("That chat was not found. Ask your question again, then send it to support.", 404);

        var user = await _db.ConversationMessages.Where(m => m.ConversationId == convo.Id && m.Role == "user").OrderByDescending(m => m.Id).FirstOrDefaultAsync(ct)
            ?? throw new SupportRejectedException("There is no question in this chat yet.", 400);
        var reply = await _db.ConversationMessages.Where(m => m.ConversationId == convo.Id && m.Role == "assistant" && m.Id > user.Id).OrderBy(m => m.Id).FirstOrDefaultAsync(ct);
        var question = Clean(user.Content, MaxQuestion);
        if (question.Length == 0) throw new SupportRejectedException("There is no question in this chat yet.", 400);

        var sender = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId)))[..16].ToLowerInvariant();
        var today = DateTime.UtcNow.Date;
        var recent = await _db.SupportRequests.Where(s => s.SenderHash == sender && s.CreatedUtc >= today).ToListAsync(ct);
        // Pressing the button twice must not create two requests.
        var same = recent.FirstOrDefault(s => s.Status == "open" && s.Question == question && s.ApplicationId == convo.ApplicationId);
        if (same != null) return same;
        if (recent.Count >= DailyPerSender) throw new SupportRejectedException("You have already sent several requests today. Please wait for a reply.", 429);
        if (await _db.SupportRequests.CountAsync(s => s.Status == "open", ct) >= MaxOpenPerCompany)
            throw new SupportRejectedException("The support inbox is full right now. Please try again later.", 409);

        var req = new SupportRequest
        {
            TenantId = tenantId, ApplicationId = convo.ApplicationId, Channel = Clean(channel, 200), Question = question,
            WasAnswered = reply?.Grounded ?? false,
            Note = Clean(note, MaxNote) is { Length: > 0 } n ? n : null,
            Contact = Clean(contact, MaxContact) is { Length: > 0 } c ? c : null,
            SenderHash = sender
        };
        _db.SupportRequests.Add(req);
        await _db.SaveChangesAsync(ct);
        return req;
    }
}
