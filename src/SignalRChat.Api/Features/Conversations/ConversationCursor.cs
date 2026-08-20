using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace SignalRChat.Api.Features.Conversations;

internal readonly record struct ConversationCursor(DateTimeOffset CreatedAtUtc, Guid Id)
{
    public string Encode()
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{CreatedAtUtc.UtcTicks}:{Id:D}");

        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    }

    public static bool TryDecode(string value, out ConversationCursor cursor)
    {
        cursor = default;

        try
        {
            var decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value));
            var separator = decoded.IndexOf(':');

            if (separator <= 0
                || !long.TryParse(
                    decoded.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var utcTicks)
                || !Guid.TryParse(decoded.AsSpan(separator + 1), out var id)
                || utcTicks < DateTimeOffset.MinValue.UtcTicks
                || utcTicks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            cursor = new ConversationCursor(
                new DateTimeOffset(utcTicks, TimeSpan.Zero),
                id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
