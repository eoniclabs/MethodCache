using System;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.KeyGenerators;
using MethodCache.Core.Runtime;

var keyGen = new SmartKeyGenerator();
var descriptor = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty, CachePolicyFields.None);
var key = keyGen.GenerateKey("GetUserProfileAsync", new object[] { 456 }, descriptor);
Console.WriteLine($"Generated key: '{key}'");
