using Hako.Core;

namespace Hako;

internal enum ProfilePreviewMode
{
    Account,
    NoMinecraft,
    Guest,
    Fixture,
    Builds
}

internal enum PreviewPage
{
    Default,
    Builds,
    Create,
    Profile,
    SettingsGame
}

internal sealed record ProfilePreviewOptions(ProfilePreviewMode Mode, int? ClientWidth, int? ClientHeight, PreviewPage Page = PreviewPage.Default)
{
    public static ProfilePreviewOptions? Parse(string[] args)
    {
        var enabled = false;
        var mode = ProfilePreviewMode.Account;
        int? width = null;
        int? height = null;
        var page = PreviewPage.Default;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--profile-preview")
            {
                enabled = true;
                if (index + 1 < args.Length && TryParseMode(args[index + 1], out var parsedMode))
                {
                    mode = parsedMode;
                    index++;
                }
            }
            else if (argument.StartsWith("--profile-preview=", StringComparison.Ordinal))
            {
                enabled = true;
                if (!TryParseMode(argument[18..], out mode))
                {
                    throw new ArgumentException("Profile preview mode must be account, no-minecraft, guest, fixture, or builds.", nameof(args));
                }
            }
            else if (argument == "--preview-page")
            {
                page = index + 1 < args.Length ? args[++index] switch
                {
                    "builds" => PreviewPage.Builds,
                    "create" => PreviewPage.Create,
                    "profile" => PreviewPage.Profile,
                    "settings-game" => PreviewPage.SettingsGame,
                    _ => throw new ArgumentException("Preview page must be builds, create, profile, or settings-game.", nameof(args))
                } : throw new ArgumentException("Preview page is missing.", nameof(args));
            }
            else if (argument is "--preview-width" or "--preview-height")
            {
                var minimum = argument == "--preview-height" ? 480 : 640;
                if (index + 1 >= args.Length || !int.TryParse(args[++index], out var value) || value < minimum || value > 2560)
                {
                    throw new ArgumentException($"Preview {argument[10..]} must be an integer from {minimum} to 2560.", nameof(args));
                }

                if (argument == "--preview-width")
                {
                    width = value;
                }
                else
                {
                    height = value;
                }
            }
        }

        return enabled ? new(mode, width, height, page) : null;
    }

    public AccountSession? CreateAccount() => Mode switch
    {
        ProfilePreviewMode.Guest => null,
        ProfilePreviewMode.NoMinecraft => CreateFakeAccount(minecraftName: null),
        _ => CreateFakeAccount("PreviewPlayer")
    };

    private static AccountSession CreateFakeAccount(string? minecraftName) => new(
        Xuid: "preview-xuid",
        Gamertag: "HakoPreview",
        Email: "preview@example.test",
        XboxAvatarUrl: null,
        MinecraftProfileId: null,
        MinecraftName: minecraftName,
        MicrosoftRefreshToken: string.Empty,
        MinecraftAccessToken: null,
        MinecraftTokenExpiresAt: null);

    private static bool TryParseMode(string value, out ProfilePreviewMode mode)
    {
        mode = value switch
        {
            "account" => ProfilePreviewMode.Account,
            "no-minecraft" => ProfilePreviewMode.NoMinecraft,
            "guest" => ProfilePreviewMode.Guest,
            "fixture" => ProfilePreviewMode.Fixture,
            "builds" => ProfilePreviewMode.Builds,
            _ => (ProfilePreviewMode)(-1)
        };
        return Enum.IsDefined(mode);
    }
}

internal sealed class InMemoryPreviewAccountStore(AccountSession? account) : IAccountStore
{
    private AccountSession? _account = account;

    public Task<AccountSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_account);
    }

    public Task<bool> SaveAsync(
        AccountSession account,
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(commitIfCurrent(() => _account = account));
    }

    public Task<bool> ClearAsync(
        Func<Action, bool> commitIfCurrent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(commitIfCurrent(() => _account = null));
    }
}

internal sealed class InMemoryPreviewUserSettingsStore : IUserSettingsStore
{
    private UserSettings? _settings;

    public Task<UserSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_settings);
    }

    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class PreviewAuthService : IMicrosoftAuthService
{
    public Task<DeviceCode> RequestDeviceCodeAsync(CancellationToken cancellationToken) => Unsupported<DeviceCode>();
    public Task<MicrosoftToken> WaitForTokenAsync(DeviceCode deviceCode, CancellationToken cancellationToken) => Unsupported<MicrosoftToken>();
    public Task<AccountSession> SignInAsync(MicrosoftToken token, CancellationToken cancellationToken) => Unsupported<AccountSession>();
    public Task<MinecraftConnection> ConnectMinecraftAsync(string refreshToken, Func<string, CancellationToken, Task> persistRefreshToken, CancellationToken cancellationToken) => Unsupported<MinecraftConnection>();

    private static Task<T> Unsupported<T>() => Task.FromException<T>(new NotSupportedException("Sign-in is disabled in profile preview mode."));
}
