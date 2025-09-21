using System;
using MethodCache.Core.KeyGenerators;
using MethodCache.Core.Configuration;

var keyGen = new SmartKeyGenerator();
var key = keyGen.GenerateKey("GetUserProfileAsync", new object[] { 456 }, new CacheMethodSettings());
Console.WriteLine($"Generated key: '{key}'");
