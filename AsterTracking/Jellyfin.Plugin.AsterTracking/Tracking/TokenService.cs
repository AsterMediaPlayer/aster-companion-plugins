namespace Jellyfin.Plugin.AsterTracking.Tracking;

/// <summary>
/// A person's token for a service, refreshed first when it's close to running out, for the scrobbler and the
/// discovery lists alike. A refresh the service refuses signs the account out.
/// </summary>
public sealed class TokenService
{
    private readonly AccountStore _store;

    public TokenService(AccountStore store)
    {
        _store = store;
    }

    /// <summary>The token, refreshed when it has less than <paramref name="ahead"/> left; null when the person hasn't linked the service.</summary>
    public async Task<string?> TokenAsync(Guid userId, ITracker tracker, TimeSpan ahead)
    {
        var account = tracker.Account(await _store.GetAsync(userId).ConfigureAwait(false));
        if (account is null)
        {
            return null;
        }

        if (account.Expires is DateTimeOffset expires && expires - DateTimeOffset.UtcNow < ahead)
        {
            LinkedAccount? fresh;
            try
            {
                fresh = await tracker.RefreshAsync(account, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SignedOutException)
            {
                await _store.UpdateAsync(userId, t => tracker.SetAccount(t, null)).ConfigureAwait(false);
                throw;
            }

            if (fresh is not null)
            {
                await _store.UpdateAsync(userId, t =>
                {
                    // Kept only if the account wasn't unlinked meanwhile; its settings as they are now.
                    if (tracker.Account(t) is LinkedAccount now)
                    {
                        fresh.Scrobble = now.Scrobble;
                        tracker.SetAccount(t, fresh);
                    }
                }).ConfigureAwait(false);
                return fresh.AccessToken;
            }
        }

        return account.AccessToken;
    }
}
