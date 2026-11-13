using System.Globalization;

namespace Hygieia;

public static class Text
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static string FormatBytes(long n)
    {
        double value = Math.Max(0, n);
        foreach (var unit in new[] { "bytes", "KB", "MB", "GB", "TB", "PB" })
        {
            if (value < 1024 || unit == "PB")
            {
                if (unit == "bytes")
                    return ((long)value).ToString("N0", En) + " " + unit;
                return value.ToString("N1", En) + " " + unit;
            }
            value /= 1024;
        }
        return $"{n:N0} bytes";
    }

    public static string FormatDuration(double seconds)
    {
        var total = Math.Max(0, (int)seconds);
        if (total < 60)
            return $"{total} sec";
        var minutes = total / 60;
        var secs = total % 60;
        if (minutes < 60)
            return $"{minutes} min {secs} sec";
        var hours = minutes / 60;
        minutes %= 60;
        return $"{hours} hr {minutes} min";
    }

    public static string Comma(long n) => n.ToString("N0", En);

    public static string Friendly(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is NativeException native)
            {
                if (native.Win32Error is 5 or 13)
                    return "Access was denied.";
                if (native.Win32Error is 32 or 33)
                    return "Another program is using this item.";
                if (native.Win32Error is 112 or 39)
                    return "The disk is full.";
                if (!string.IsNullOrWhiteSpace(native.Message))
                    return native.Message;
            }
        }
        if (ex is UnauthorizedAccessException)
            return "Access was denied.";
        if (ex is IOException && IsDiskFull(ex))
            return "The disk is full.";
        return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    }

    public static bool IsDiskFull(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is NativeException native && native.Win32Error is 112 or 39)
                return true;
            var hr = current.HResult & 0xFFFF;
            if (hr is 112 or 39)
                return true;
        }
        return false;
    }
}

public sealed class CancelledException : Exception
{
    public CancelledException() : base("Cancelled.") { }
}

public interface IReporter
{
    void Log(string message, bool error = false);
    void Progress(double fraction, string detail);
    bool IsCancelled { get; }
}

public sealed class Pulse
{
    readonly double _interval;
    long _last;

    public Pulse(double interval = 0.08) => _interval = interval;

    public bool Ready(bool force = false)
    {
        var now = Environment.TickCount64;
        if (force || now - _last >= _interval * 1000)
        {
            _last = now;
            return true;
        }
        return false;
    }
}

public sealed class ListReporter : IReporter
{
    public List<string> Logs { get; } = new();
    public List<string> Errors { get; } = new();
    public string Detail { get; private set; } = "";
    public double Fraction { get; private set; }
    public bool IsCancelled { get; private set; }

    public void Log(string message, bool error = false)
    {
        Logs.Add(message);
        if (error)
            Errors.Add(message);
    }

    public void Progress(double fraction, string detail)
    {
        Fraction = fraction;
        Detail = detail;
    }

    public void Cancel() => IsCancelled = true;
}
