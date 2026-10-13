# Release compatibility notes

These changes are being made before the first nuget.org release.

## HTTP cache key format

Length framing changes **every HTTP response cache key**, including requests without `Vary` headers. Existing entries created by an older build will be cold misses after upgrading; the new build does not reuse them. This affects built-in and custom HTTP cache storage. Let old entries expire or clear their storage namespace during deployment.

## Distributed invalidation format

Backplane key and tag messages use new message types and lossless UTF-16 encoding. Invalidations do not cross between old and new wire versions in either direction. Upgrade every node sharing a cache and backplane together, with a coordinated cache clear, or use a new cache namespace. Old distributed envelopes are cache misses.

MessagePack keys preserve derived member data in nested base-class/interface slots and collection elements. Earlier keys for these arguments change; existing entries become cold misses and expire normally.

Overloaded generic methods include generic arity in their policy ids and key prefixes: `Get(int)`, `Get``1(int)` and `Get``2(int)` remain distinct. Generator and runtime configuration use the same ids; earlier keys for overloaded generic methods become cold misses.
