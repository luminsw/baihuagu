namespace MobileContract.Quota;

/// <summary>
/// 设备配额信息
/// </summary>
public record QuotaInfoDto
{
    public int NoteLimit { get; init; }
    public int NotesUsed { get; init; }
    public int VaultLimit { get; init; }
    public int VaultsUsed { get; init; }
    public int StreakDays { get; init; }
    public bool InGracePeriod { get; init; }
    public int RolloverNotes { get; init; }
    public int PaidSyncQuota { get; init; }
    public int AiBuildQuota { get; init; }
    public decimal TotalSpent { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<ProductInfoDto>? Products { get; init; }
}
