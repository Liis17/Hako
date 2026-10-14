namespace Hako.Core;

public sealed record DeviceCode(
    string DeviceCodeValue,
    string UserCode,
    Uri VerificationUri,
    int ExpiresInSeconds,
    int PollIntervalSeconds);
