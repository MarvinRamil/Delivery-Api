using BeeLogistics.Modules.Notification.Domain;

namespace BeeLogistics.Modules.Notification.Application.Interfaces;

public interface ISmsRecordRepository
{
    Task AddAsync(SmsRecord record, CancellationToken ct = default);

    Task<(IReadOnlyList<SmsRecord> Items, int TotalCount)> GetPagedAsync(
        SmsStatus? status = null,
        string? to = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default);
}
