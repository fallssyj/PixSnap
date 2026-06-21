using System;
using System.Text;

namespace PixSnap.Services;

internal static class ExceptionMessageFormatter
{
    public static string Format(string prefix, Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine(prefix);

        var current = exception;
        var depth = 0;
        while (current is not null)
        {
            builder.AppendLine($"[{depth}] {current.GetType().FullName}");
            builder.AppendLine(current.Message);
            builder.AppendLine($"HRESULT: 0x{current.HResult:X8}");

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                builder.AppendLine("StackTrace:");
                builder.AppendLine(current.StackTrace);
            }

            current = current.InnerException;
            depth++;
            if (current is not null)
                builder.AppendLine("InnerException:");
        }

        return builder.ToString().TrimEnd();
    }
}
