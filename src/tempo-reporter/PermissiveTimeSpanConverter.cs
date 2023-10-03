using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
using TimeSpanParserUtil;

public class PermissiveTimeSpanConverter : ITypeConverter
{
    public object? ConvertFromString(string? text, IReaderRow row, MemberMapData memberMapData)
    {
        return TimeSpanParser.Parse(text);
    }

    public string? ConvertToString(object? value, IWriterRow row, MemberMapData memberMapData)
    {
        if (value is TimeSpan ts)
            return ts.ToString();
        return null;
    }
}