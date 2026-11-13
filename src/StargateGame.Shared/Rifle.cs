using System.Numerics;
namespace StargateGame.Shared;

public static class Rifle
{
    public const int Capacity = 30, FireTicks = 8, ReloadTicks = 164;
    public const float Range = 300;
    public static bool Valid(WeaponItem item) => item.Magazine is >= 0 and <= Capacity && item.Reserve is >= 0 and <= 120;
    public static bool CanReload(WeaponItem item) => item.Reserve > 0 && (item.Magazine < Capacity || !item.Chambered);
    public static WeaponItem Fire(WeaponItem item) => !item.Chambered ? item : item with
    { Magazine = Math.Max(0, item.Magazine-1), Chambered = item.Magazine > 0 };
    public static WeaponItem Reload(WeaponItem item)
    {
        int added = Math.Min(Capacity-item.Magazine, item.Reserve);
        int magazine = item.Magazine+added;
        bool chamber = item.Chambered || magazine > 0;
        if (!item.Chambered && chamber) magazine--;
        return item with { Magazine = magazine, Chambered = chamber, Reserve = item.Reserve-added };
    }
}
public sealed record RifleShot(long Id, int Player, long Tick, Vector3 Origin, Vector3 End, bool Hit, int Body);
