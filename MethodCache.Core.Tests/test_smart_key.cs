using System;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;

var keyGen = new SmartKeyGenerator();
var descriptor = CacheRuntimePolicy.FromPolicy("test", CachePolicy.Empty, CachePolicyFields.None);
var key = keyGen.GenerateKey("GetUserProfileAsync", new object[] { 456 }, descriptor);
Console.WriteLine($"Generated key: '{key}'");
