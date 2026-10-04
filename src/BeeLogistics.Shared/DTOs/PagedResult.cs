namespace BeeLogistics.Shared.DTOs;

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;

    public PagedResult() { }

    public PagedResult(List<T> items, int totalCount, int page, int pageSize)
    {
        Items = items;
        TotalCount = totalCount;
        Page = page;
        PageSize = pageSize;
    }
}

public class PagedQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 10;
    
    public int Skip => (Page - 1) * PageSize;
}
