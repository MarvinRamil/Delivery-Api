namespace BeeLogistics.Modules.Giveaways.Domain;

public enum GiveawayEntryMode
{
    Manual = 0,
    PerDelivery = 1
}

public enum GiveawayEntrySource
{
    Manual = 0,
    DeliveryCompletion = 1
}

public enum GiveawayStatus
{
    Draft = 0,
    Active = 1,
    Drawn = 2,
    Closed = 3
}
