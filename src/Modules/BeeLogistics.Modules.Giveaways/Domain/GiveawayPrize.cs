using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Giveaways.Domain;

public class GiveawayPrize : Entity
{
    public Guid GiveawayId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public int Quantity { get; private set; }
    public int Tier { get; private set; }

    public Giveaway Giveaway { get; private set; } = default!;

    private GiveawayPrize() { }

    public GiveawayPrize(Guid giveawayId, string name, string? description, int quantity, int tier)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Prize name is required.");
        if (quantity < 1)
            throw new InvalidOperationException("Prize quantity must be at least 1.");

        GiveawayId = giveawayId;
        Name = name.Trim();
        Description = description?.Trim();
        Quantity = quantity;
        Tier = tier;
        CreatedAt = DateTime.UtcNow;
    }

    public void Update(string name, string? description, int quantity, int tier)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Prize name is required.");
        if (quantity < 1)
            throw new InvalidOperationException("Prize quantity must be at least 1.");

        Name = name.Trim();
        Description = description?.Trim();
        Quantity = quantity;
        Tier = tier;
        UpdatedAt = DateTime.UtcNow;
    }
}
