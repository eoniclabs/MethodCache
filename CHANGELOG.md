# Changelog

Changes that a user of the packages notices. The release steps are in
[docs/developer/RELEASE.md](docs/developer/RELEASE.md); when you tag a release, copy its section into the GitHub
release.

## Unreleased (1.0.0)

### If you used a pre-release build from GitHub Packages

- **HTTP cache keys.** Keys are now length-framed, so every HTTP response cache key changes, including for requests
  without `Vary` headers. Entries written by a pre-release build are not read again; this applies to the built-in and
  to custom HTTP cache storage. Let them expire, or clear the storage namespace when you deploy.
- **Distributed invalidation.** Backplane key and tag messages use new message types and lossless UTF-16 encoding,
  and distributed cache entries use envelope version 4. Invalidations do not cross between a pre-release build and
  1.0.0 in either direction: a node ignores backplane messages from another MethodCache version and logs one warning,
  and entries written by a pre-release build are cache misses. Upgrade every node that shares a cache and backplane
  together, with a cache clear, or use a new cache namespace.
