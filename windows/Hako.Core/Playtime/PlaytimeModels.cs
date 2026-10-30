using Hako.Core.Minecraft;

namespace Hako.Core.Playtime;

public sealed class PlaytimeLedger
{
    public int FormatVersion { get; set; } = 2;
    public string? HakoAccountId { get; set; }
    public Dictionary<Guid, HakoPlaytimeUpload> Uploads { get; set; } = [];
    public Dictionary<string, PlaytimeOwnerRecord> Owners { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, PlaytimeSessionRecord> Sessions { get; set; } = [];
    public PlaytimePendingTransfer? PendingTransfer { get; set; }
    public HashSet<Guid> AppliedTransfers { get; set; } = [];
}

public sealed class PlaytimeOwnerRecord
{
    public double TotalSeconds { get; set; }
    public Dictionary<Guid, double> Instances { get; set; } = [];
}

public sealed class PlaytimeSessionRecord
{
    public Guid SessionID { get; set; }
    public Guid InstanceID { get; set; }
    public string OwnerKey { get; set; } = "guest";
    public double CreditedSeconds { get; set; }
    public string? HakoAccountId { get; set; }
    public Guid SegmentId { get; set; } = Guid.NewGuid();
    public double HakoSeconds { get; set; }
}

public sealed class PlaytimePendingTransfer
{
    public Guid ID { get; set; }
    public string TargetXuid { get; set; } = string.Empty;
    public double GuestSecondsAtBoundary { get; set; }
    public Dictionary<Guid, double> PerInstanceAtBoundary { get; set; } = [];
    public Guid[] SessionIDs { get; set; } = [];
}

public sealed class PlaytimeSessionJournal
{
    public Guid SessionID { get; set; }
    public Guid InstanceID { get; set; }
    public string OwnerKey { get; set; } = "guest";
    public GameProcessRecord? Process { get; set; }
    public PlaytimeProcessIdentity? Helper { get; set; }
    public ulong StartedUnbiasedTicks { get; set; }
    public double BaseElapsedSeconds { get; set; }
    public double ElapsedSeconds { get; set; }
    public bool IsFinished { get; set; }
}

public sealed record PlaytimeProcessIdentity(int Pid, ulong StartSeconds, ulong StartMicroseconds);

public sealed record PlaytimeSnapshot(double TotalSeconds, IReadOnlyDictionary<Guid, double> Instances);

public sealed class HakoPlaytimeUpload
{
    public Guid SessionId { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public long DurationMs { get; set; }
}
