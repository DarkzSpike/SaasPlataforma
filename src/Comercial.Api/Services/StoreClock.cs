using Comercial.Api.Security;
using Comercial.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Comercial.Api.Services;

/// <summary>
/// "Hoje" e "este mês" no fuso da loja (não em UTC): uma venda às 22h em São Paulo pertence ao dia local.
/// </summary>
public static class StoreClock
{
    public const string DefaultZone = "America/Sao_Paulo";

    public static async Task<TimeZoneInfo> ZoneAsync(AppDbContext db, RequestActor actor)
    {
        var q = db.Stores.AsNoTracking().Where(s => s.IsActive);
        if (actor.UserStoreId is { } id) q = q.Where(s => s.Id == id);
        var zoneId = await q.OrderBy(s => s.CreatedAt).Select(s => s.TimeZoneId).FirstOrDefaultAsync() ?? DefaultZone;
        return Find(zoneId);
    }

    public static TimeZoneInfo Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone(DefaultZone, TimeSpan.FromHours(-3), DefaultZone, DefaultZone); }
    }

    public static DateTimeOffset StartOfDay(DateTimeOffset utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        return ToUtc(local.Date, tz);
    }

    public static DateTimeOffset StartOfMonth(DateTimeOffset utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        return ToUtc(new DateTime(local.Year, local.Month, 1), tz);
    }

    private static DateTimeOffset ToUtc(DateTime localMidnight, TimeZoneInfo tz) =>
        new DateTimeOffset(localMidnight, tz.GetUtcOffset(localMidnight)).ToUniversalTime();
}
