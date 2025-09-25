namespace MethodCache.HttpCaching.Options;

public class CacheVariationOptions
{
    public bool EnableQualityValues { get; set; } = true;
    public bool EnableAcceptLanguage { get; set; } = true;
    public bool EnableAcceptEncoding { get; set; } = true;
    public bool EnableAcceptCharset { get; set; } = false;
    public bool EnableMultipleVariants { get; set; } = true;
    public int MaxVariantsPerUrl { get; set; } = 5;
    public string[] AdditionalVaryHeaders { get; set; } = Array.Empty<string>();
}
