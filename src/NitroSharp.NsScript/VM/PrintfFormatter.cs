using System;
using System.Globalization;
using System.Text;

namespace NitroSharp.NsScript.VM;

internal static class PrintfFormatter
{
    public static string Format(string format, object[] arguments, out bool invalid)
    {
        var output = new StringBuilder();
        invalid = false;
        int argument = 0;
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '%') { output.Append(format[i]); continue; }
            int start = i++;
            if (i < format.Length && format[i] == '%') { output.Append('%'); continue; }
            bool left = i < format.Length && format[i] == '-';
            if (left) i++;
            bool zero = i < format.Length && format[i] == '0';
            int width = 0;
            int widthStart = i;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
                width = Math.Min(10000, width * 10 + format[i++] - '0');
            bool widthOnly = i > widthStart && !left;
            int precision = -1;
            if (i < format.Length && format[i] == '.')
            {
                widthOnly = false;
                precision = 0; i++;
                while (i < format.Length && char.IsAsciiDigit(format[i]))
                    precision = Math.Min(99, precision * 10 + format[i++] - '0');
            }
            // Legacy scripts use a terminal width such as %02 as decimal shorthand.
            bool decimalShorthand = i == format.Length && widthOnly
                && argument < arguments.Length && arguments[argument] is float;
            char specifier = decimalShorthand ? 'd' : i < format.Length ? format[i] : '\0';
            if ("disuxXfFgG".IndexOf(specifier) < 0 || argument >= arguments.Length)
            {
                invalid = true;
                output.Append(format.AsSpan(start, Math.Min(i + 1, format.Length) - start));
                continue;
            }
            object value = arguments[argument++];
            string text;
            try
            {
                text = specifier switch
                {
                    's' => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    'd' or 'i' => ToInteger(value).ToString(CultureInfo.InvariantCulture),
                    'u' => unchecked((uint)ToInteger(value)).ToString(CultureInfo.InvariantCulture),
                    'x' or 'X' => ToInteger(value).ToString(specifier.ToString(), CultureInfo.InvariantCulture),
                    _ => Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString(
                        char.ToUpperInvariant(specifier) + (precision >= 0 ? precision.ToString(CultureInfo.InvariantCulture) : specifier is 'f' or 'F' ? "6" : string.Empty), CultureInfo.InvariantCulture)
                };
                if (specifier == 's' && precision >= 0 && text.Length > precision) text = text[..precision];
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                invalid = true;
                output.Append(format.AsSpan(start, Math.Min(i + 1, format.Length) - start));
                continue;
            }
            if (left) text = text.PadRight(width);
            else if (zero && text.StartsWith('-')) text = "-" + text[1..].PadLeft(Math.Max(0, width - 1), '0');
            else text = text.PadLeft(width, zero ? '0' : ' ');
            output.Append(text);
        }
        return output.ToString();
    }

    private static long ToInteger(object value)
        => Convert.ToInt64(Math.Truncate(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
}
