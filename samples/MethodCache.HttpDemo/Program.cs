using MethodCache.HttpDemo;

Console.WriteLine("HttpClient caching against a real local HTTP server. No external API is needed.");
var checks = await HttpExamples.RunAsync();
foreach (var check in checks) Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Claim}");
return checks.All(check => check.Passed) ? 0 : 1;
