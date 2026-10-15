# Body-budget allocation probe isolation

Historical net10 head 7a3b9a837bea92c873f6d44cd96ea48b85f34832, CI run
20261015-191543-7a3b9a837bea, failed Linux diffcoverage in
`BodyBudget_DoesNotMaterializeTemporaryTargetStrings` for
`client:abc:m:GET|generation:v`: expected zero, observed 7,696 bytes. The ordinary
Linux test job and local pre-change coverage probe passed. Original output is retained;
there is no deterministic local reproduction or proven source for those extra bytes.

The fixture now runs its synchronous measurement on an owned thread without inherited
ExecutionContext, in a NoInlining helper. Store/entry setup, thread setup/completion,
async test scaffolding and assertions are outside the counter. It keeps **the original
100 warmups and one batch of 1,000 calls**, exact zero bytes, the exact budget sum and
the subsequent stored UrlTarget assertion. There is no minimum, tolerance, extra batch
or adaptive retry. All three original input shapes remain present.

A temporary production mutation replaces `TargetSpan(key).Length` with
`Target(key).Length`, recreating the temporary target string whose absence is asserted.
All three cases fail with exactly 48,000 bytes, while budget arithmetic remains the
same. Production is then restored byte for byte. The complete 320-case HTTP suite
passes with XPlat Code Coverage under default, tr-TR and sv-SE on macOS ARM64/net10.
These local results are not Linux latest-head CI acceptance or section-10 timing.
The original failure remains unexplained; the subsequent three-platform CI must test
the forwarded fixture. No shipped source, benchmark or public API changes.

Text logs use repository LF normalization. `files-sha256.json` records stored digests.
The mutation script records the exact temporary change and unconditional restoration;
its absolute paths describe this execution, not a portable test command.
