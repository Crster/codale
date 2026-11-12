namespace Codale.App.Services;

/// <summary>Keeps an account profile until its credentials have been signed out successfully.</summary>
public static class AccountProfileRemoval
{
    public static async Task<bool> RemoveAsync(ByokProvider profile,
        Func<ByokProvider, Task<bool>> signOut, Action<ByokProvider> remove)
    {
        if (!profile.IsAccount) throw new ArgumentException("Only account profiles can be signed out.", nameof(profile));
        if (!await signOut(profile)) return false;
        remove(profile);
        return true;
    }
}
