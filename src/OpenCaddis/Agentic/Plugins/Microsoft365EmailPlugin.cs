using System.ComponentModel;
using System.Text;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Messages;
using Microsoft.Graph.Users.Item.MailFolders;
using OpenCaddis.Agentic;
using OpenCaddis.Services;

namespace OpenCaddis.Agentic.Plugins;

[PluginAlias("Microsoft365Email")]
public sealed class Microsoft365EmailPlugin : IFabrCorePlugin
{
    private IFabrCoreAgentHost? _host;
    private ILogger<Microsoft365EmailPlugin> _logger = null!;
    private Microsoft365AuthService _authService = null!;
    private int _maxResults = 25;
    private int _maxBodyLength = 10_000;

    public Task InitializeAsync(AgentConfiguration config, IServiceProvider serviceProvider)
    {
        _host = serviceProvider.GetService<IFabrCoreAgentHost>();
        _logger = serviceProvider.GetRequiredService<ILogger<Microsoft365EmailPlugin>>();
        _authService = serviceProvider.GetRequiredService<Microsoft365AuthService>();

        var maxResultsSetting = config.GetPluginSetting("Microsoft365Email", "MaxResults");
        if (int.TryParse(maxResultsSetting, out var maxResults))
            _maxResults = maxResults;

        var maxBodySetting = config.GetPluginSetting("Microsoft365Email", "MaxBodyLength");
        if (int.TryParse(maxBodySetting, out var maxBody))
            _maxBodyLength = maxBody;

        _logger.LogInformation("Microsoft365EmailPlugin initialized (maxResults: {MaxResults}, connected: {Connected})", _maxResults, _authService.IsConnected);
        return Task.CompletedTask;
    }

    [Description("List emails from a mail folder with optional filtering. Returns subject, sender, date, and message ID.")]
    public async Task<string> ListEmails(
        [Description("Mail folder to list from: Inbox, SentItems, Drafts, DeletedItems, Archive, JunkEmail (default: Inbox)")] string folder = "Inbox",
        [Description("Filter: all, unread, or read (default: all)")] string filter = "all",
        [Description("Number of emails to return (default: 25)")] int count = 0,
        [Description("Number of emails to skip for paging (default: 0)")] int skip = 0)
    {
        if (!CheckConnected(out var error)) return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Listing emails from {folder}...");

        if (count <= 0) count = _maxResults;

        try
        {
            var client = await _authService.GetGraphClientAsync();

            var folderId = MapFolderName(folder);
            var request = client.Me.MailFolders[folderId].Messages;

            var messages = await request.GetAsync(cfg =>
            {
                cfg.QueryParameters.Top = count;
                cfg.QueryParameters.Skip = skip;
                cfg.QueryParameters.Select = ["id", "subject", "from", "receivedDateTime", "isRead", "hasAttachments", "importance"];
                cfg.QueryParameters.Orderby = ["receivedDateTime desc"];

                if (filter == "unread")
                    cfg.QueryParameters.Filter = "isRead eq false";
                else if (filter == "read")
                    cfg.QueryParameters.Filter = "isRead eq true";
            });

            if (messages?.Value is null || messages.Value.Count == 0)
            {
                _logger.LogDebug("No emails found in {Folder} (filter: {Filter})", folder, filter);
                return $"No emails found in {folder}" + (filter != "all" ? $" (filter: {filter})" : "") + ".";
            }

            _logger.LogDebug("Listed {Count} email(s) from {Folder} (filter: {Filter}, skip: {Skip})", messages.Value.Count, folder, filter, skip);

            var sb = new StringBuilder();
            sb.AppendLine($"**{folder}** — {messages.Value.Count} email(s)" + (skip > 0 ? $" (skipped {skip})" : "") + ":\n");

            foreach (var msg in messages.Value)
            {
                var readIcon = msg.IsRead == true ? " " : "* ";
                var attachment = msg.HasAttachments == true ? " [attachment]" : "";
                var importance = msg.Importance == Importance.High ? " [!]" : "";
                var from = msg.From?.EmailAddress?.Name ?? msg.From?.EmailAddress?.Address ?? "Unknown";
                var date = msg.ReceivedDateTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";

                sb.AppendLine($"{readIcon}**{msg.Subject ?? "(no subject)"}**{importance}{attachment}");
                sb.AppendLine($"  From: {from} | {date}");
                sb.AppendLine($"  ID: `{msg.Id}`");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return HandleGraphError(ex);
        }
    }

    [Description("Search emails using keywords (Microsoft Graph KQL search). Returns matching emails with subject, sender, date, and ID.")]
    public async Task<string> SearchEmails(
        [Description("Search query (supports keywords, from:, subject:, etc.)")] string query,
        [Description("Maximum number of results (default: 25)")] int count = 0)
    {
        if (!CheckConnected(out var error)) return error;

        if (string.IsNullOrWhiteSpace(query))
            return "Error: Search query cannot be empty.";

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, $"Searching emails for \"{query}\"...");

        if (count <= 0) count = _maxResults;

        try
        {
            var client = await _authService.GetGraphClientAsync();

            var messages = await client.Me.Messages.GetAsync(cfg =>
            {
                cfg.QueryParameters.Search = $"\"{query}\"";
                cfg.QueryParameters.Top = count;
                cfg.QueryParameters.Select = ["id", "subject", "from", "receivedDateTime", "isRead", "hasAttachments", "bodyPreview"];
            });

            if (messages?.Value is null || messages.Value.Count == 0)
            {
                _logger.LogDebug("Email search returned no results for '{Query}'", query);
                return $"No emails found matching \"{query}\".";
            }

            _logger.LogDebug("Email search returned {Count} result(s) for '{Query}'", messages.Value.Count, query);

            var sb = new StringBuilder();
            sb.AppendLine($"**Search results for \"{query}\"** — {messages.Value.Count} email(s):\n");

            foreach (var msg in messages.Value)
            {
                var from = msg.From?.EmailAddress?.Name ?? msg.From?.EmailAddress?.Address ?? "Unknown";
                var date = msg.ReceivedDateTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
                var preview = msg.BodyPreview ?? "";
                if (preview.Length > 150) preview = preview[..150] + "...";

                sb.AppendLine($"**{msg.Subject ?? "(no subject)"}**");
                sb.AppendLine($"  From: {from} | {date}");
                sb.AppendLine($"  Preview: {preview}");
                sb.AppendLine($"  ID: `{msg.Id}`");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return HandleGraphError(ex);
        }
    }

    [Description("Read the full content of an email by its message ID. Returns headers, body, and attachment info.")]
    public async Task<string> ReadEmail(
        [Description("The message ID (from ListEmails or SearchEmails results)")] string messageId,
        [Description("Whether to mark the email as read (default: true)")] bool markAsRead = true)
    {
        if (!CheckConnected(out var error)) return error;

        if (string.IsNullOrWhiteSpace(messageId))
            return "Error: Message ID cannot be empty.";

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Reading email...");

        try
        {
            var client = await _authService.GetGraphClientAsync();

            var msg = await client.Me.Messages[messageId].GetAsync(cfg =>
            {
                cfg.QueryParameters.Select = [
                    "id", "subject", "from", "toRecipients", "ccRecipients",
                    "receivedDateTime", "sentDateTime", "isRead", "importance",
                    "hasAttachments", "body", "bodyPreview"
                ];
                cfg.QueryParameters.Expand = ["attachments($select=name,size,contentType)"];
            });

            if (msg is null)
            {
                _logger.LogDebug("Email not found: {MessageId}", messageId);
                return "Error: Email not found.";
            }

            _logger.LogDebug("Reading email: {Subject} (ID: {MessageId})", msg.Subject, messageId);

            // Mark as read if requested
            if (markAsRead && msg.IsRead != true)
            {
                try
                {
                    await client.Me.Messages[messageId].PatchAsync(new Message { IsRead = true });
                }
                catch { /* non-critical */ }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"**Subject:** {msg.Subject ?? "(no subject)"}");
            sb.AppendLine($"**From:** {FormatRecipient(msg.From?.EmailAddress)}");

            if (msg.ToRecipients?.Count > 0)
                sb.AppendLine($"**To:** {string.Join(", ", msg.ToRecipients.Select(r => FormatRecipient(r.EmailAddress)))}");

            if (msg.CcRecipients?.Count > 0)
                sb.AppendLine($"**CC:** {string.Join(", ", msg.CcRecipients.Select(r => FormatRecipient(r.EmailAddress)))}");

            sb.AppendLine($"**Date:** {msg.ReceivedDateTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown"}");

            if (msg.Importance != Importance.Normal)
                sb.AppendLine($"**Importance:** {msg.Importance}");

            // Attachments
            if (msg.Attachments?.Count > 0)
            {
                sb.AppendLine($"**Attachments:** {msg.Attachments.Count}");
                foreach (var att in msg.Attachments)
                {
                    var size = att.Size.HasValue ? $" ({FormatFileSize(att.Size.Value)})" : "";
                    sb.AppendLine($"  - {att.Name}{size}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            // Body
            var body = msg.Body?.Content ?? msg.BodyPreview ?? "(empty)";
            if (msg.Body?.ContentType == BodyType.Html)
            {
                // Strip HTML tags for readable output
                body = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]+>", " ");
                body = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ");
                body = System.Net.WebUtility.HtmlDecode(body).Trim();
            }

            if (body.Length > _maxBodyLength)
                body = body[.._maxBodyLength] + "\n\n[Body truncated...]";

            sb.Append(body);

            return sb.ToString();
        }
        catch (Exception ex)
        {
            return HandleGraphError(ex);
        }
    }

    [Description("Send an email. Supports To, CC, BCC recipients, plain text or HTML body.")]
    public async Task<string> SendEmail(
        [Description("Comma-separated To recipients (email addresses)")] string to,
        [Description("Email subject")] string subject,
        [Description("Email body content")] string body,
        [Description("Comma-separated CC recipients (optional)")] string? cc = null,
        [Description("Comma-separated BCC recipients (optional)")] string? bcc = null,
        [Description("Whether the body is HTML (default: false, plain text)")] bool isHtml = false)
    {
        if (!CheckConnected(out var error)) return error;

        if (string.IsNullOrWhiteSpace(to))
            return "Error: At least one 'to' recipient is required.";
        if (string.IsNullOrWhiteSpace(subject))
            return "Error: Subject cannot be empty.";
        if (string.IsNullOrWhiteSpace(body))
            return "Error: Body cannot be empty.";

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Sending email...");

        try
        {
            var client = await _authService.GetGraphClientAsync();

            var message = new Message
            {
                Subject = subject,
                Body = new ItemBody
                {
                    ContentType = isHtml ? BodyType.Html : BodyType.Text,
                    Content = body
                },
                ToRecipients = ParseRecipients(to),
                CcRecipients = string.IsNullOrWhiteSpace(cc) ? [] : ParseRecipients(cc),
                BccRecipients = string.IsNullOrWhiteSpace(bcc) ? [] : ParseRecipients(bcc)
            };

            await client.Me.SendMail.PostAsync(new Microsoft.Graph.Me.SendMail.SendMailPostRequestBody
            {
                Message = message,
                SaveToSentItems = true
            });

            _logger.LogInformation("Email sent to {To}, subject: '{Subject}'", to, subject);

            var recipientList = to;
            if (!string.IsNullOrWhiteSpace(cc)) recipientList += $", CC: {cc}";
            if (!string.IsNullOrWhiteSpace(bcc)) recipientList += $", BCC: {bcc}";

            return $"Email sent successfully.\n**To:** {recipientList}\n**Subject:** {subject}";
        }
        catch (Exception ex)
        {
            return HandleGraphError(ex);
        }
    }

    [Description("List available mail folders and their unread counts.")]
    public async Task<string> GetMailFolders()
    {
        if (!CheckConnected(out var error)) return error;

        if (_host is not null) await ThinkingNotifier.SendThinkingAsync(_host, "Getting mail folders...");

        try
        {
            var client = await _authService.GetGraphClientAsync();

            var folders = await client.Me.MailFolders.GetAsync(cfg =>
            {
                cfg.QueryParameters.Top = 50;
                cfg.QueryParameters.Select = ["id", "displayName", "totalItemCount", "unreadItemCount"];
            });

            if (folders?.Value is null || folders.Value.Count == 0)
                return "No mail folders found.";

            var sb = new StringBuilder();
            sb.AppendLine($"**Mail Folders** ({folders.Value.Count}):\n");

            foreach (var folder in folders.Value)
            {
                var unread = folder.UnreadItemCount > 0 ? $" ({folder.UnreadItemCount} unread)" : "";
                sb.AppendLine($"- **{folder.DisplayName}** — {folder.TotalItemCount} items{unread}");
                sb.AppendLine($"  ID: `{folder.Id}`");
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return HandleGraphError(ex);
        }
    }

    [Description("Check the Microsoft 365 connection status and available permissions.")]
    public Task<string> GetConnectionStatus()
    {
        var sb = new StringBuilder();

        if (_authService.IsConnected)
        {
            sb.AppendLine("**Status:** Connected");
            if (_authService.UserDisplayName is not null)
                sb.AppendLine($"**User:** {_authService.UserDisplayName}");
            if (_authService.UserEmail is not null)
                sb.AppendLine($"**Email:** {_authService.UserEmail}");
            if (_authService.ConsentedScopes is { Length: > 0 })
                sb.AppendLine($"**Scopes:** {string.Join(", ", _authService.ConsentedScopes)}");
        }
        else
        {
            sb.AppendLine("**Status:** Not connected");
            sb.AppendLine("Connect to Microsoft 365 in Settings > Connections to use email tools.");
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }

    // --- Helpers ---

    private bool CheckConnected(out string error)
    {
        if (_authService.IsConnected)
        {
            error = string.Empty;
            return true;
        }

        _logger.LogWarning("Microsoft 365 operation attempted but not connected");
        error = "Not connected to Microsoft 365. Please go to Settings > Connections and connect your account first.";
        return false;
    }

    private static string MapFolderName(string folder) =>
        folder.ToLowerInvariant() switch
        {
            "inbox" => "Inbox",
            "sentitems" or "sent" => "SentItems",
            "drafts" => "Drafts",
            "deleteditems" or "deleted" or "trash" => "DeletedItems",
            "archive" => "Archive",
            "junkemail" or "junk" or "spam" => "JunkEmail",
            _ => folder // Allow raw folder IDs
        };

    private static List<Recipient> ParseRecipients(string addresses) =>
        addresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(addr => new Recipient
            {
                EmailAddress = new EmailAddress { Address = addr }
            })
            .ToList();

    private static string FormatRecipient(EmailAddress? email)
    {
        if (email is null) return "Unknown";
        if (!string.IsNullOrEmpty(email.Name))
            return $"{email.Name} <{email.Address}>";
        return email.Address ?? "Unknown";
    }

    private static string FormatFileSize(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    private string HandleGraphError(Exception ex)
    {
        _logger.LogWarning(ex, "Microsoft Graph API error");
        if (ex is Microsoft.Graph.Models.ODataErrors.ODataError odataError)
        {
            var code = odataError.Error?.Code ?? "";
            var message = odataError.Error?.Message ?? ex.Message;

            if (code == "ErrorAccessDenied" || ex.Message.Contains("403"))
                return $"Error: Access denied. You may need additional permissions. Details: {message}";

            if (code == "ErrorItemNotFound")
                return "Error: The requested item was not found. It may have been moved or deleted.";

            return $"Error: Microsoft Graph API error ({code}): {message}";
        }

        if (ex is InvalidOperationException)
            return $"Error: {ex.Message}";

        return $"Error: {ex.Message}";
    }
}
