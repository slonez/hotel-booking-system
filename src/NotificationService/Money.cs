using System.Globalization;

namespace NotificationService;

public static class Money
{
    public static string Format(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture) + " EUR";
}
