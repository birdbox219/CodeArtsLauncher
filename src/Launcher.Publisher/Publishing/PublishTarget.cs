namespace Launcher.Publisher.Publishing;

/// <summary>
/// A wharf target: <c>owner/slug:channel</c>. Owner is per-game, never a single global username —
/// two of the six games are collaborations hosted under other accounts.
/// </summary>
public sealed record PublishTarget(string Owner, string Slug, string Channel)
{
    /// <summary>What butler is actually given on the command line.</summary>
    public string Full => $"{Owner}/{Slug}:{Channel}";

    /// <summary>The same game without the channel, which is what `butler status` wants: asking
    /// about <c>owner/slug</c> lists every channel, asking about a channel that does not exist
    /// yet just says so.</summary>
    public string GameTarget => $"{Owner}/{Slug}";

    /// <summary>
    /// Accepts what a person would type: a bare slug, <c>owner/slug</c>, or a full
    /// <c>owner/slug:channel</c>, filling the gaps from the defaults.
    /// </summary>
    public static PublishTarget Parse(string input, string defaultOwner, string defaultChannel)
    {
        string text = (input ?? "").Trim();
        if (text.Length == 0)
            throw new ArgumentException("No game given.", nameof(input));

        string channel = defaultChannel;
        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            channel = text[(colon + 1)..].Trim();
            text = text[..colon].Trim();
        }

        string owner = defaultOwner;
        int slash = text.IndexOf('/');
        if (slash >= 0)
        {
            owner = text[..slash].Trim();
            text = text[(slash + 1)..].Trim();
        }

        if (owner.Length == 0) throw new ArgumentException("No owner given.", nameof(input));
        if (text.Length == 0) throw new ArgumentException("No game slug given.", nameof(input));
        if (channel.Length == 0) throw new ArgumentException("No channel given.", nameof(input));

        return new PublishTarget(owner, text, channel);
    }
}
