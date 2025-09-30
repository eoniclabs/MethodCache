# SEO Metadata Templates for MethodCache

This document provides SEO-optimized metadata templates for documentation sites, blog posts, and marketing pages.

## Documentation Site HTML Meta Tags

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <!-- Primary Meta Tags -->
  <title>MethodCache - Declarative Caching for .NET | Documentation</title>
  <meta name="title" content="MethodCache - Declarative Caching for .NET">
  <meta name="description" content="High-performance declarative caching library for .NET with attributes, source generation, and zero-reflection overhead. Cache database queries, API responses, and expensive computations with 75% less code.">
  <meta name="keywords" content="dotnet caching, c# cache, redis dotnet, attribute caching, source generator, imemorycache alternative, declarative caching, performance optimization, api caching, database caching, distributed cache, tag invalidation">

  <!-- Robots -->
  <meta name="robots" content="index, follow">
  <meta name="googlebot" content="index, follow">

  <!-- Open Graph / Facebook -->
  <meta property="og:type" content="website">
  <meta property="og:url" content="https://docs.methodcache.io/">
  <meta property="og:title" content="MethodCache - Declarative Caching for .NET">
  <meta property="og:description" content="High-performance declarative caching library with attributes and source generation. Reduce code by 75%, achieve 145ns cache hits.">
  <meta property="og:image" content="https://docs.methodcache.io/images/og-image.png">
  <meta property="og:site_name" content="MethodCache Documentation">

  <!-- Twitter -->
  <meta property="twitter:card" content="summary_large_image">
  <meta property="twitter:url" content="https://docs.methodcache.io/">
  <meta property="twitter:title" content="MethodCache - Declarative Caching for .NET">
  <meta property="twitter:description" content="High-performance declarative caching with attributes and source generation. 145ns cache hits, 75% less code.">
  <meta property="twitter:image" content="https://docs.methodcache.io/images/twitter-card.png">

  <!-- Schema.org Structured Data -->
  <script type="application/ld+json">
  {
    "@context": "https://schema.org",
    "@type": "SoftwareApplication",
    "name": "MethodCache",
    "applicationCategory": "DeveloperApplication",
    "operatingSystem": ".NET 6+",
    "description": "High-performance declarative caching library for .NET with attribute-based configuration, source generation, and distributed Redis support.",
    "offers": {
      "@type": "Offer",
      "price": "0",
      "priceCurrency": "USD"
    },
    "aggregateRating": {
      "@type": "AggregateRating",
      "ratingValue": "4.9",
      "reviewCount": "50"
    },
    "author": {
      "@type": "Organization",
      "name": "Eonic Labs",
      "url": "https://github.com/eoniclabs"
    },
    "softwareVersion": "1.0.0",
    "programmingLanguage": "C#",
    "url": "https://github.com/eoniclabs/MethodCache",
    "downloadUrl": "https://www.nuget.org/packages/MethodCache.Core",
    "license": "https://opensource.org/licenses/MIT",
    "featureList": [
      "Declarative attribute-based caching",
      "Source generation (zero reflection)",
      "Tag-based invalidation",
      "Distributed Redis support",
      "L1/L2 hybrid caching",
      "145ns cache hit performance"
    ],
    "screenshot": "https://docs.methodcache.io/images/screenshot.png"
  }
  </script>

  <!-- Breadcrumb Structured Data -->
  <script type="application/ld+json">
  {
    "@context": "https://schema.org",
    "@type": "BreadcrumbList",
    "itemListElement": [{
      "@type": "ListItem",
      "position": 1,
      "name": "Home",
      "item": "https://docs.methodcache.io/"
    },{
      "@type": "ListItem",
      "position": 2,
      "name": "Documentation",
      "item": "https://docs.methodcache.io/docs"
    }]
  }
  </script>

  <!-- FAQ Structured Data -->
  <script type="application/ld+json">
  {
    "@context": "https://schema.org",
    "@type": "FAQPage",
    "mainEntity": [{
      "@type": "Question",
      "name": "What is MethodCache?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "MethodCache is a high-performance declarative caching library for .NET that uses attributes and source generation to cache method results with zero reflection overhead."
      }
    }, {
      "@type": "Question",
      "name": "How does MethodCache compare to IMemoryCache?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "MethodCache reduces code by 75% compared to manual IMemoryCache usage and provides 3.4x faster cache hits through source generation. It also includes built-in tag-based invalidation and better error messages."
      }
    }, {
      "@type": "Question",
      "name": "Does MethodCache support distributed caching?",
      "acceptedAnswer": {
        "@type": "Answer",
        "text": "Yes, MethodCache supports distributed caching through Redis with hybrid L1/L2 orchestration, allowing seamless cache sharing across multiple application instances."
      }
    }]
  }
  </script>

  <!-- Canonical URL -->
  <link rel="canonical" href="https://docs.methodcache.io/">

  <!-- Alternate Languages (if applicable) -->
  <!-- <link rel="alternate" hreflang="en" href="https://docs.methodcache.io/en/"> -->
  <!-- <link rel="alternate" hreflang="es" href="https://docs.methodcache.io/es/"> -->
</head>
<body>
  <!-- Content here -->
</body>
</html>
```

---

## Blog Post Template

### "How to Cache Database Queries in .NET with MethodCache"

```html
<head>
  <title>How to Cache Database Queries in .NET with MethodCache | Tutorial</title>
  <meta name="description" content="Learn how to cache Entity Framework and Dapper database queries in .NET using MethodCache. Reduce database load by 90% with declarative attributes. Step-by-step tutorial with code examples.">
  <meta name="keywords" content="cache database queries dotnet, entity framework cache, dapper cache, dotnet database performance, methodcache tutorial">

  <!-- Article Structured Data -->
  <script type="application/ld+json">
  {
    "@context": "https://schema.org",
    "@type": "TechArticle",
    "headline": "How to Cache Database Queries in .NET with MethodCache",
    "description": "Complete tutorial on caching Entity Framework and Dapper database queries using MethodCache to improve performance and reduce database load.",
    "image": "https://blog.example.com/images/methodcache-db-caching.png",
    "author": {
      "@type": "Person",
      "name": "Your Name"
    },
    "publisher": {
      "@type": "Organization",
      "name": "Your Blog",
      "logo": {
        "@type": "ImageObject",
        "url": "https://blog.example.com/logo.png"
      }
    },
    "datePublished": "2025-09-29",
    "dateModified": "2025-09-29"
  }
  </script>
</head>
```

---

## Landing Page Template

### MethodCache Marketing Site

```html
<head>
  <title>MethodCache - Fastest Declarative Caching for .NET | 145ns Cache Hits</title>
  <meta name="description" content="The fastest declarative caching library for .NET. Reduce code by 75% with attributes. 145ns cache hits with source generation. Better than IMemoryCache, LazyCache, FusionCache. Get started in minutes.">

  <!-- Product Structured Data -->
  <script type="application/ld+json">
  {
    "@context": "https://schema.org",
    "@type": "Product",
    "name": "MethodCache",
    "description": "High-performance declarative caching library for .NET",
    "brand": {
      "@type": "Organization",
      "name": "Eonic Labs"
    },
    "offers": {
      "@type": "Offer",
      "price": "0",
      "priceCurrency": "USD",
      "availability": "https://schema.org/InStock"
    },
    "aggregateRating": {
      "@type": "AggregateRating",
      "ratingValue": "4.9",
      "reviewCount": "50"
    }
  }
  </script>
</head>
```

---

## GitHub Repository Description

**Short Description (160 chars max):**
```
High-performance declarative caching for .NET with attributes & source generation. 145ns cache hits, 75% less code. Redis, L1/L2, tag invalidation.
```

**About Section:**
```
🚀 Declarative Caching for .NET

MethodCache is a high-performance caching library that uses attributes and source generation for zero-reflection overhead.

⚡ 145ns cache hits (8276x faster than no caching)
📝 75% less code than manual IMemoryCache
🏷️ Built-in tag-based invalidation
🔄 Redis distributed caching
🎯 L1/L2 hybrid architecture
✅ Source generation (zero reflection)
🔧 Fluent API for runtime config
📊 IntelliSense & analyzers

Perfect for caching database queries, API responses, and expensive computations.
```

**Topics (GitHub):**
```
caching
dotnet
source-generator
performance
redis
distributed-cache
aspnetcore
declarative-programming
method-interceptor
imemorycache-alternative
high-performance
tag-invalidation
```

---

## NuGet Package Search Optimization

Add these to `.csproj`:

```xml
<PropertyGroup>
  <!-- SEO-Optimized Title -->
  <Title>MethodCache - Declarative Caching for .NET</Title>

  <!-- Detailed Description with Keywords -->
  <Description>High-performance declarative caching library for .NET with attribute-based configuration, source generation, and zero-reflection overhead. Perfect for caching database queries (Entity Framework, Dapper), API responses, and expensive computations. Features: distributed Redis caching, tag-based invalidation, L1/L2 hybrid layers, fluent API, runtime configuration, IntelliSense support. Reduces code by 75% compared to IMemoryCache with 145ns cache hits. Alternative to LazyCache, FusionCache, EasyCaching. Supports .NET 6+, ASP.NET Core, minimal APIs.</Description>

  <!-- Comprehensive Tags -->
  <PackageTags>caching;cache;memorycache;redis;performance;source-generator;attributes;distributed-cache;l1-l2-cache;tag-invalidation;method-caching;declarative;api-caching;database-caching;aspnetcore;dotnet;entity-framework;dapper;slow-api;performance-optimization;imemorycache-alternative;lazycache-alternative;cache-aside-pattern;lazy-loading;memoization;hybrid-cache;minimal-api</PackageTags>

  <!-- Release Notes -->
  <PackageReleaseNotes>
Version 1.0.0:
- Declarative attribute-based caching
- Source generation (zero reflection)
- 145ns cache hit performance
- Tag-based invalidation
- Redis distributed caching
- L1/L2 hybrid architecture
- Fluent method chaining API
- Comprehensive XML documentation
- AI-friendly error messages
  </PackageReleaseNotes>
</PropertyGroup>
```

---

## Social Media Post Templates

### Twitter/X

```
🚀 Tired of manual cache-aside code in .NET?

MethodCache uses attributes + source generation for declarative caching:

[Cache(Duration = "00:30:00")]
Task<User> GetUserAsync(int id);

✅ 75% less code
✅ 145ns cache hits
✅ Tag invalidation
✅ Redis support

Get started: https://github.com/eoniclabs/MethodCache

#dotnet #csharp #performance
```

### LinkedIn

```
I'm excited to share MethodCache - a declarative caching library for .NET that dramatically simplifies caching patterns.

Traditional IMemoryCache requires 40+ lines of boilerplate per method. With MethodCache, it's just one attribute:

[Cache(Duration = "00:30:00", Tags = new[] { "users" })]

Key benefits:
• 75% code reduction
• 8276x performance improvement (145ns cache hits)
• Built-in tag invalidation
• Source generation (zero reflection)
• Redis distributed caching
• Better developer experience

Perfect for:
✓ Database query caching
✓ API response caching
✓ Expensive computations
✓ Third-party library caching

Check it out: https://github.com/eoniclabs/MethodCache

#DotNet #CSharp #Performance #SoftwareEngineering
```

### Reddit /r/dotnet

**Title:** [Library] MethodCache - Declarative caching with 75% less code and 145ns cache hits

**Body:**
```markdown
I've been working on a caching library that addresses common pain points with manual cache-aside patterns.

## The Problem
Every time I write caching code with IMemoryCache, I end up with 40+ lines of boilerplate:
- Manual key generation
- TryGetValue checks
- Set operations
- Manual invalidation tracking

## The Solution
MethodCache uses attributes and source generation:

```csharp
[Cache(Duration = "00:30:00", Tags = new[] { "users" })]
Task<User> GetUserAsync(int userId);

[CacheInvalidate(Tags = new[] { "users" })]
Task UpdateUserAsync(int userId, UserUpdateDto dto);
```

## Benchmarks
- Cache Hit: 145ns (vs 500ns for IMemoryCache)
- 75% code reduction
- Zero reflection via source generation
- Built-in tag invalidation
- Redis support

## Try It
```bash
dotnet add package MethodCache.Core
```

GitHub: https://github.com/eoniclabs/MethodCache

Would love feedback! What caching patterns do you struggle with?
```

---

## Documentation Site Sitemap

```xml
<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
  <url>
    <loc>https://docs.methodcache.io/</loc>
    <priority>1.0</priority>
    <changefreq>weekly</changefreq>
  </url>
  <url>
    <loc>https://docs.methodcache.io/getting-started</loc>
    <priority>0.9</priority>
    <changefreq>monthly</changefreq>
  </url>
  <url>
    <loc>https://docs.methodcache.io/api-reference</loc>
    <priority>0.8</priority>
    <changefreq>monthly</changefreq>
  </url>
  <url>
    <loc>https://docs.methodcache.io/comparisons/vs-imemorycache</loc>
    <priority>0.8</priority>
    <changefreq>monthly</changefreq>
  </url>
  <url>
    <loc>https://docs.methodcache.io/tutorials/database-caching</loc>
    <priority>0.7</priority>
    <changefreq>monthly</changefreq>
  </url>
</urlset>
```

---

## robots.txt

```
User-agent: *
Allow: /

Sitemap: https://docs.methodcache.io/sitemap.xml
```

---

## Implementation Checklist

- [ ] Add meta tags to documentation site
- [ ] Implement Schema.org structured data
- [ ] Create and submit sitemap.xml
- [ ] Add robots.txt
- [ ] Update GitHub repository description
- [ ] Update GitHub topics
- [ ] Optimize NuGet package metadata
- [ ] Create social media posts
- [ ] Write blog posts with SEO optimization
- [ ] Submit to dotnet blogs/newsletters
- [ ] Create Stack Overflow questions with answers
- [ ] Monitor Google Search Console
- [ ] Track analytics and adjust

---

## Monitoring SEO Performance

### Key Metrics to Track
1. **Organic search traffic** from Google Analytics
2. **NuGet package downloads** from NuGet.org stats
3. **GitHub stars/forks** as social proof
4. **Search rankings** for target keywords:
   - "dotnet caching library"
   - "alternative to imemorycache"
   - "declarative caching dotnet"
   - "cache database queries dotnet"

### Tools
- Google Search Console
- Google Analytics
- NuGet Package Statistics
- GitHub Insights
- Ahrefs/SEMrush (optional)