namespace MethodCache.TestSupport;

public static class DurationCorpus
{
    public static IEnumerable<object[]> Values()
    {
        yield return ["30s", 300000000L];
        yield return ["1.5h", 54000000000L];
        yield return ["1d", 864000000000L];
        yield return [" 2M ", 1200000000L];
        yield return ["0.0000001s", 1L];
        yield return ["1.00000000000000000000000000000s", 10000000L];
        yield return ["0.000000005m", 3L];
        yield return ["0.00000000025h", 9L];
        yield return ["0.00000000003125d", 27L];
        yield return ["922337203685.4775807s", long.MaxValue];
    }

    /// <summary>A TimeSpan is rejected, and the error suggests the same value with a unit.</summary>
    public static IEnumerable<object[]> TimeSpans()
    {
        yield return ["00:10:00", "10m"];
        yield return ["01:30:00", "90m"];
        yield return ["1.00:00:00", "1d"];
        yield return ["00:00:00.2", "0.2s"];
        yield return ["00:00:00.0000001", "0.0000001s"];
        yield return ["10675199.02:48:05.4775807", "922337203685.4775807s"];
    }
    public static IEnumerable<object[]> Invalid()
    {
        foreach (var text in new[] { "", " ", "30", "0s", "-1h", "1m30s", "1,5h", "1e3s", "NaNs", "Infinitys", "0.00000001s", "0.00000019s", "0.000000001m", "0.00000000001h", "0.000000003m", "0.00000000005h", "0.0000000000013d", "1.00000000000000000000000000001s", "+1s", "1 s", ".5s", "1.s", "999999999999999999999d", "10675199.02:48:05.4775808", "1:00:00", "00:60:00", "00:00:60", "00:00:00", "off", "00:00:00.00000001", "00:10:00", "1.00:00:00", "00:00:00.0000001", "10675199.02:48:05.4775807", "922337203685.4775808s" })
            yield return [text];
    }
}
