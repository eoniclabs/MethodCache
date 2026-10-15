namespace MethodCache.TestSupport;

public static class DurationCorpus
{
    public static IEnumerable<object[]> Values()
    {
        yield return ["30s", 300000000L];
        yield return ["1.5h", 54000000000L];
        yield return ["1d", 864000000000L];
        yield return ["1.00:00:00", 864000000000L];
        yield return ["00:00:00.0000001", 1L];
        yield return [" 2M ", 1200000000L];
        yield return ["0.0000001s", 1L];
        yield return ["0.00000019s", 1L];
        yield return ["10675199.02:48:05.4775807", long.MaxValue];
    }
    public static IEnumerable<object[]> Invalid()
    {
        foreach (var text in new[] { "", " ", "30", "0s", "-1h", "1m30s", "1,5h", "1e3s", "NaNs", "Infinitys", "0.00000001s", "+1s", "1 s", ".5s", "1.s", "999999999999999999999d", "10675199.02:48:05.4775808", "1:00:00", "00:60:00", "00:00:60", "00:00:00", "off", "00:00:00.00000001" })
            yield return [text];
    }
}
