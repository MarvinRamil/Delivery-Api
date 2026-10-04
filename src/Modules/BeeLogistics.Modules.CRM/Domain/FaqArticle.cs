using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.CRM.Domain;

public class FaqArticle : Entity
{
    public string Title { get; set; } = null!;
    public string Content { get; set; } = null!;
    public string Category { get; set; } = null!;
    public int SortOrder { get; set; }
    public bool IsPublished { get; set; } = true;
    public int ViewCount { get; set; }
    public int HelpfulCount { get; set; }
    public int NotHelpfulCount { get; set; }
    public string? Tags { get; set; } // For search
}

public class FaqCategory : Entity
{
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
