using Notch.Core.Activities;

namespace Notch.Core.Shelf;

public static class ShelfActivities
{
    public const string AddedId = "shelf.added";

    /// <summary>The notice for files dropped on the pill while the notch stayed closed, so the drop is seen to have landed.</summary>
    public static Activity Added(int count) => new()
    {
        Id = AddedId,
        Tier = ActivityTier.Transient,
        Title = "On the shelf",
        Detail = count == 1 ? "1 item" : $"{count} items",
        Glyph = "",
        Glow = new Glow(GlowColor.White, GlowPattern.Flash, 0.6),
        Lifetime = TimeSpan.FromSeconds(3.5),
    };
}
